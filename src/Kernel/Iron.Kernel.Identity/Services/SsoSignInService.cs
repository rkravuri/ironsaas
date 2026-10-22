using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>A sign-in through a provider under way: where to send the browser, and the token of the sign-in cookie.</summary>
public sealed record SsoStart(Uri AuthorizationUrl, string ChallengeToken, DateTimeOffset ExpiresAt);

/// <summary>What the provider's answer is checked against, kept in the sign-in's challenge.</summary>
internal sealed record SsoExchange(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("verifier")] string Verifier,
    [property: JsonPropertyName("redirect_uri")] string RedirectUri,
    [property: JsonPropertyName("return_to")] string? ReturnTo);

/// <summary>
/// Signing in through a tenant's OpenID Connect provider (TF-014, ADR-0048): the authorization code flow with PKCE (S256),
/// <c>state</c> and <c>nonce</c>, the browser holding the sign-in by the HttpOnly sign-in cookie. The code is exchanged
/// with the client secret from the store, and the ID token checked in full. Then the people rules: only an email the
/// provider marks verified, of one of the provider's allowed domains, matches or creates anyone; a person already linked
/// is found by the provider's subject; a person whose verified email matches is linked at their first sign-in; a new
/// person is created only where the provider creates people, with its default role and home unit; a deactivated person
/// is never re-created or reactivated. Every refusal is audited with its reason, and the person is told what they can do.
/// </summary>
public sealed class SsoSignInService(
    IdentityDbContext context,
    OidcDiscovery discovery,
    IdTokenValidator validator,
    ISsoClientSecrets secrets,
    AuthenticationChallenges challenges,
    LoginService login,
    IRoleAssignments roleAssignments,
    IProvidedAssignments provided,
    IOrganisationUnits units,
    SecurityEventLog securityEvents,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    IHttpClientFactory factory,
    TimeProvider time,
    ILogger<SsoSignInService> logger)
{
    public const string FailedMessage = "The sign-in through your company login could not be completed. Start again.";

    public const string CallbackPath = "/sso/callback";

    /// <summary>OpenID Connect's scopes for the subject, the email and its verification, and the name.</summary>
    public const string Scopes = "openid email profile";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The providers the sign-in page offers, by name.</summary>
    public async Task<IReadOnlyList<SsoProvider>> ProvidersAsync(CancellationToken cancellationToken) =>
        await context.SsoProviders.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The browser goes to the provider (ADR-0048): a new <c>state</c>, <c>nonce</c> and PKCE verifier, kept in a sign-in
    /// challenge of five minutes; the authorization address carries the state, the nonce and the verifier's S256 hash.
    /// </summary>
    public async Task<Result<SsoStart>> StartAsync(Guid providerId, string redirectUri, string? returnTo, string? loginHint, CancellationToken cancellationToken)
    {
        var provider = await context.SsoProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == providerId, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            return Error.NotFound(IdentityErrors.SsoProviderNotFound, "This company login is not set up here. Sign in another way, or ask your administrator.");
        }

        var metadata = await discovery.MetadataAsync(provider.DiscoveryUrl, cancellationToken).ConfigureAwait(false);
        if (metadata.IsFailure)
        {
            return Error.Unavailable(IdentityErrors.SsoUnavailable, $"{provider.Name} cannot be reached now. Try again in a moment, or sign in another way.");
        }

        var exchange = new SsoExchange(SecureTokens.New(), SecureTokens.New(), SecureTokens.New(), redirectUri, SafeReturn(returnTo));
        var (challenge, token) = await challenges.StartForBrowserAsync(ChallengePurposes.SsoSignIn, null, cancellationToken).ConfigureAwait(false);
        challenge.ThroughProvider(provider.Id, JsonSerializer.Serialize(exchange, Json));
        await challenges.SaveAsync(cancellationToken).ConfigureAwait(false);

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = provider.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scopes,
            ["state"] = exchange.State,
            ["nonce"] = exchange.Nonce,
            ["code_challenge"] = CodeChallenge(exchange.Verifier),
            ["code_challenge_method"] = "S256",
        };
        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            query["login_hint"] = loginHint.Trim();
        }

        var endpoint = metadata.Value.AuthorizationEndpoint.AbsoluteUri;
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = new Uri(endpoint + separator + string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
        return new SsoStart(url, token, challenge.ExpiresAt);
    }

    /// <summary>
    /// The provider sent the browser back, and the shell's callback page posts what it brought: the sign-in's challenge is
    /// found by its cookie and ended at once, so an answer works once; the state must be the one sent; the code is
    /// exchanged with the PKCE verifier and the client secret; the ID token is checked in full; then the people rules,
    /// the provider's group mapping and the tenant's MFA policy decide what follows.
    /// </summary>
    public async Task<Result<LoginOutcome>> CompleteAsync(HttpContext http, string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var challenge = await challenges.FindForBrowserAsync(http, [ChallengePurposes.SsoSignIn], cancellationToken).ConfigureAwait(false);
        if (challenge?.ProviderId is not { } providerId || challenge.Exchange is null)
        {
            return Error.Unauthorized(IdentityErrors.SignInExpired, "This sign-in took too long, or ended. Start again.");
        }

        var exchange = JsonSerializer.Deserialize<SsoExchange>(challenge.Exchange, Json)!;
        await challenges.EndAsync(challenge, cancellationToken).ConfigureAwait(false);
        var provider = await context.SsoProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == providerId, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            return Error.NotFound(IdentityErrors.SsoProviderNotFound, "This company login is not set up here any more. Sign in another way.");
        }

        if (state is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(exchange.State)))
        {
            return await RefuseAsync(provider, null, null, "state", IdentityErrors.SsoFailed, FailedMessage, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
        {
            return await RefuseAsync(provider, null, null, $"provider_error:{Trim(error ?? "no_code", 60)}", IdentityErrors.SsoFailed, $"{provider.Name} did not sign you in. Start again, or ask your administrator.", cancellationToken).ConfigureAwait(false);
        }

        var metadata = await discovery.MetadataAsync(provider.DiscoveryUrl, cancellationToken).ConfigureAwait(false);
        if (metadata.IsFailure)
        {
            return Error.Unavailable(IdentityErrors.SsoUnavailable, $"{provider.Name} cannot be reached now. Try again in a moment.");
        }

        var secret = await secrets.ReadAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(secret))
        {
            logger.LogError("The client secret of single sign-on provider {ProviderId} is not in the store.", provider.Id);
            return Error.Unavailable(IdentityErrors.SsoUnavailable, $"{provider.Name} is not fully set up here. Ask your administrator to save its client secret again.");
        }

        var exchanged = await ExchangeAsync(metadata.Value, provider, code, exchange, secret, cancellationToken).ConfigureAwait(false);
        if (exchanged.Failure is { } exchangeFailure)
        {
            return await RefuseAsync(provider, null, null, exchangeFailure, IdentityErrors.SsoFailed, FailedMessage, cancellationToken).ConfigureAwait(false);
        }

        var checkedToken = await validator.CheckAsync(
            exchanged.IdToken!,
            new IdTokenExpectations(metadata.Value.Issuer, provider.ClientId, exchange.Nonce, provider.EmailClaim, provider.NameClaim, provider.GroupsClaim),
            (refresh, ct) => discovery.KeysAsync(metadata.Value, refresh, ct),
            cancellationToken).ConfigureAwait(false);
        if (checkedToken.Identity is not { } identity)
        {
            return await RefuseAsync(provider, null, null, $"id_token:{checkedToken.Failure}", IdentityErrors.SsoFailed, FailedMessage, cancellationToken).ConfigureAwait(false);
        }

        var admitted = await AdmitAsync(provider, identity, cancellationToken).ConfigureAwait(false);
        if (admitted.IsFailure)
        {
            return admitted.Error;
        }

        var principal = admitted.Value;
        var wanted = provider.RoleMappings
            .Where(m => identity.Groups.Contains(m.Group, StringComparer.Ordinal))
            .Select(m => new ProvidedRole(m.RoleCode, m.UnitId is { } unit ? RoleScope.Unit(unit, m.IncludeChildren) : RoleScope.Tenant))
            .ToList();
        var synced = await provided.SyncAsync(principal.Id, provider.AssignmentSource, wanted, cancellationToken).ConfigureAwait(false);
        if (synced.IsFailure)
        {
            logger.LogWarning("The roles of provider {ProviderId} could not be brought in step for principal {PrincipalId}: {Error}", provider.Id, principal.Id, synced.Error);
        }

        var outcome = await login.ProviderSignedInAsync(principal, provider.Id, identity.MultiFactor && provider.TrustProviderMfa, cancellationToken).ConfigureAwait(false);
        return outcome.IsSuccess ? outcome.Value with { ReturnTo = exchange.ReturnTo } : outcome;
    }

    /// <summary>
    /// The people rules (ADR-0048), in order: a verified email; of an allowed domain; the person linked to the provider's
    /// subject; or the person whose login identifier or email is that address, linked now; or a new person where the
    /// provider creates people. A deactivated person is refused at every turn.
    /// </summary>
    private async Task<Result<Principal>> AdmitAsync(SsoProvider provider, IdTokenIdentity identity, CancellationToken cancellationToken)
    {
        var email = identity.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email))
        {
            return await RefuseAsync(provider, null, null, "no_email", IdentityErrors.SsoEmailUnverified, $"{provider.Name} did not give your email address, so it cannot sign you in here. Ask your administrator.", cancellationToken).ConfigureAwait(false);
        }

        if (!identity.EmailVerified)
        {
            return await RefuseAsync(provider, null, email, "email_unverified", IdentityErrors.SsoEmailUnverified, $"{provider.Name} has not verified your email address {email}, so it cannot sign you in here. Verify it there, or ask your administrator.", cancellationToken).ConfigureAwait(false);
        }

        if (!provider.AllowsEmail(email))
        {
            return await RefuseAsync(provider, null, email, "domain_not_allowed", IdentityErrors.SsoDomainNotAllowed, $"{provider.Name} signed you in as {email}, but this organisation does not sign people of {SsoProvider.DomainOf(email)} in through it. Ask your administrator.", cancellationToken).ConfigureAwait(false);
        }

        var now = time.GetUtcNow();
        var link = await context.SsoLinks.SingleOrDefaultAsync(l => l.ProviderId == provider.Id && l.Subject == identity.Subject, cancellationToken).ConfigureAwait(false);
        if (link is not null)
        {
            var linked = await context.Principals.SingleAsync(p => p.Id == link.PrincipalId, cancellationToken).ConfigureAwait(false);
            if (linked.IsDeactivated || !linked.CanSignInThroughProvider)
            {
                return await RefuseAsync(provider, linked, email, "deactivated", IdentityErrors.SsoDeactivated, Deactivated(provider), cancellationToken).ConfigureAwait(false);
            }

            link.SignedIn(email, now);
            linked.ActivatedByProvider();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return linked;
        }

        var matches = await context.Principals
            .Where(p => p.Type == PrincipalType.User && (p.LoginId == email || (p.Email != null && p.Email.ToLower() == email)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var person = matches.FirstOrDefault(p => p.LoginId == email) ?? (matches.Count == 1 ? matches[0] : null);
        if (person is null && matches.Count > 1)
        {
            return await RefuseAsync(provider, null, email, "ambiguous_email", IdentityErrors.SsoNoAccount, $"More than one account here has the email {email}. Ask your administrator to sort them out.", cancellationToken).ConfigureAwait(false);
        }

        if (person is not null)
        {
            if (person.IsDeactivated || !person.CanSignInThroughProvider)
            {
                return await RefuseAsync(provider, person, email, "deactivated", IdentityErrors.SsoDeactivated, Deactivated(provider), cancellationToken).ConfigureAwait(false);
            }

            if (await context.SsoLinks.AnyAsync(l => l.ProviderId == provider.Id && l.PrincipalId == person.Id, cancellationToken).ConfigureAwait(false))
            {
                // Linked to another of the provider's accounts: the email moved from one to the other there.
                return await RefuseAsync(provider, person, email, "linked_to_another_subject", IdentityErrors.SsoFailed, $"Your account here is linked to another {provider.Name} account. Ask your administrator.", cancellationToken).ConfigureAwait(false);
            }

            await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
            await LinkAsync(person, provider, identity.Subject, email, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Principal {PrincipalId} was linked to single sign-on provider {ProviderId}.", person.Id, provider.Id);
            return person;
        }

        if (!provider.JustInTime)
        {
            return await RefuseAsync(provider, null, email, "no_account", IdentityErrors.SsoNoAccount, $"{provider.Name} signed you in as {email}, but you have no account here. Ask your administrator.", cancellationToken).ConfigureAwait(false);
        }

        return await CreateAsync(provider, identity, email, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A new employee of an allowed domain (TF-014 AC1): active from the start, with no password, in the provider's
    /// default home unit (the root when it has none) and with its default role there and below. One transaction with the
    /// link and the audit events; the default role through the trusted path, as the seed's, the rule having been applied
    /// when the provider was saved.
    /// </summary>
    private async Task<Result<Principal>> CreateAsync(SsoProvider provider, IdTokenIdentity identity, string email, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var home = provider.DefaultHomeUnitId is { } unitId ? await units.FindAsync(unitId, cancellationToken).ConfigureAwait(false) : await units.RootAsync(cancellationToken).ConfigureAwait(false);
        if (home is not { Status: UnitStatus.Active })
        {
            return await RefuseAsync(provider, null, email, "home_unit", IdentityErrors.SsoNoAccount, $"{provider.Name} would create your account in a unit that is closed. Ask your administrator.", cancellationToken).ConfigureAwait(false);
        }

        var name = string.IsNullOrWhiteSpace(identity.Name) ? email : identity.Name.Trim();
        var person = new Principal(email, PrincipalType.User, name.Length > Principal.DisplayNameMaxLength ? name[..Principal.DisplayNameMaxLength] : name, email);
        person.SetHomeUnit(home.Id);
        person.ActivatedByProvider();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.Principals.Add(person);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["display_name"] = person.DisplayName,
            ["home_unit_id"] = home.Id.ToString(),
            ["source"] = "sso",
            ["provider_id"] = provider.Id.ToString(),
            ["default_role"] = provider.DefaultRoleCode ?? string.Empty,
        };
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.UserCreated, AuditOutcome.Success, person.Id, person.LoginId, details, UserService.RecordType, person.Id), cancellationToken).ConfigureAwait(false);
        if (provider.DefaultRoleCode is { } role)
        {
            var assigned = await roleAssignments.AssignAsync(person.Id, role, RoleScope.Unit(home.Id, includeChildren: true), cancellationToken).ConfigureAwait(false);
            if (assigned.IsFailure)
            {
                logger.LogWarning("Provider {ProviderId} created principal {PrincipalId} without its default role {Role}: {Error}", provider.Id, person.Id, role, assigned.Error);
            }
        }

        await LinkAsync(person, provider, identity.Subject, email, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Single sign-on provider {ProviderId} created principal {PrincipalId}.", provider.Id, person.Id);
        return person;
    }

    /// <summary>The link, and the person's security event and its audit event (ADR-0048).</summary>
    private async Task LinkAsync(Principal person, SsoProvider provider, string subject, string email, DateTimeOffset now, CancellationToken cancellationToken)
    {
        person.ActivatedByProvider();
        context.SsoLinks.Add(new SsoLink(person.Id, provider.Id, subject, email, now));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["provider_id"] = provider.Id.ToString(), ["provider"] = provider.Name, ["email"] = email };
        await securityEvents.RecordAsync(person, SecurityEventKinds.SsoLinked, SecurityEventActors.Self, provider.Name, null, details, cancellationToken, actorId: person.Id).ConfigureAwait(false);
    }

    /// <summary>A refusal: audited as a failed sign-in with its reason, the provider and the email, and told to the person.</summary>
    private async Task<Error> RefuseAsync(SsoProvider provider, Principal? principal, string? email, string reason, string code, string message, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["method"] = "sso", ["provider_id"] = provider.Id.ToString(), ["provider"] = provider.Name };
        if (email is not null)
        {
            details["email"] = email;
        }

        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, principal?.Id ?? Guid.Empty, principal?.LoginId ?? email ?? string.Empty, details, Reason: reason), cancellationToken).ConfigureAwait(false);
        logger.LogWarning("A sign-in through single sign-on provider {ProviderId} was refused: {Reason}.", provider.Id, reason);
        return code == IdentityErrors.SsoFailed ? Error.Unauthorized(code, message) : Error.Forbidden(code, message);
    }

    private static string Deactivated(SsoProvider provider) => $"{provider.Name} signed you in, but your account here is deactivated. Ask your administrator.";

    /// <summary>
    /// The code for tokens (RFC 6749 4.1.3, RFC 7636): the redirect URI of the request, the PKCE verifier, and the client
    /// secret in the form or, for a provider that takes only that, in Basic authentication. Answers the ID token, or why not.
    /// </summary>
    private async Task<(string? IdToken, string? Failure)> ExchangeAsync(OidcMetadata metadata, SsoProvider provider, string code, SsoExchange exchange, string secret, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = exchange.RedirectUri,
            ["code_verifier"] = exchange.Verifier,
            ["client_id"] = provider.ClientId,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, metadata.TokenEndpoint);
        if (metadata.PostsClientSecret)
        {
            form["client_secret"] = secret;
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(provider.ClientId)}:{Uri.EscapeDataString(secret)}")));
        }

        request.Content = new FormUrlEncodedContent(form);
        try
        {
            using var http = factory.CreateClient(OidcDiscovery.HttpClientName);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var error = document.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return (null, $"token_exchange:{Trim(error!, 60)}");
            }

            return document.RootElement.TryGetProperty("id_token", out var idToken) && idToken.ValueKind == JsonValueKind.String
                ? (idToken.GetString(), null)
                : (null, "token_exchange:no_id_token");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("The code exchange with single sign-on provider {ProviderId} failed: {Message}", provider.Id, exception.Message);
            return (null, "token_exchange:unreachable");
        }
    }

    /// <summary>RFC 7636's S256: the verifier's SHA-256 in base64url.</summary>
    public static string CodeChallenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A path of the shell's to return to, never another site: anything else returns home.</summary>
    public static string? SafeReturn(string? returnTo) =>
        returnTo is { Length: > 0 and <= 500 } path && path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("/\\", StringComparison.Ordinal) ? path : null;

    private static string Trim(string text, int length) => text.Length > length ? text[..length] : text;
}
