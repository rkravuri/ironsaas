using System.Globalization;
using System.Text.RegularExpressions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>One check of "Test connection": what was checked, how it went (<c>passed</c>, <c>warning</c>, <c>failed</c>), and why, in words.</summary>
public sealed record SsoTestItem(string Check, string Status, string Message);

/// <summary>The result of "Test connection" (ADR-0048): every check, and whether a sign-in can work.</summary>
public sealed record SsoTestReport(IReadOnlyList<SsoTestItem> Items)
{
    public bool Passed => Items.All(i => i.Status != SsoTestStatus.Failed);
}

public static class SsoTestStatus
{
    public const string Passed = "passed";
    public const string Warning = "warning";
    public const string Failed = "failed";
}

/// <summary>
/// The tenant's single sign-on providers (TF-014, ADR-0048), managed by administrators who hold the critical
/// <c>identity.sso.manage</c>, behind a recent authentication. A provider's default role and every group mapping must
/// name a role its author may grant at its scope (ADR-0031 rule 1): a provider would otherwise give people more than the
/// administrator who set it up holds. The client secret goes to the store and nowhere else, so no row, log line or audit
/// event holds it; each change is audited with the fields that changed and whether the secret was replaced.
/// </summary>
public sealed partial class SsoProviderService(
    IdentityDbContext context,
    ISsoClientSecrets secrets,
    IProvidedAssignments provided,
    IOrganisationUnits units,
    OidcDiscovery discovery,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor)
{
    public const string RecordType = "sso_provider";

    public const int ClientSecretMaxLength = 1000;

    public async Task<IReadOnlyList<SsoProvider>> ListAsync(CancellationToken cancellationToken) =>
        await context.SsoProviders.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<Result<SsoProvider>> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await context.SsoProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false) is { } provider
            ? provider
            : Error.NotFound(IdentityErrors.SsoProviderNotFound, "There is no single sign-on provider with this id.");

    public async Task<Result<SsoProvider>> CreateAsync(SsoProviderValues values, string clientSecret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (string.IsNullOrWhiteSpace(clientSecret) || clientSecret.Length > ClientSecretMaxLength)
        {
            return Error.Validation(IdentityErrors.SsoInvalid, "The client secret is missing.", new FieldError("client_secret", "required", "Paste the client secret the provider gave you."));
        }

        var refusal = await CheckAsync(null, values, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var provider = new SsoProvider(values);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.SsoProviders.Add(provider);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Into the store before the commit: a store that refuses leaves no provider without its secret.
        await secrets.WriteAsync(provider.Id, clientSecret.Trim(), cancellationToken).ConfigureAwait(false);
        var details = Describe(provider.Values());
        details["client_secret"] = "set";
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.SsoProviderCreated, AuditOutcome.Success, actor.Id, provider.Name, details, RecordType, provider.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return provider;
    }

    /// <summary>Changes a provider; an empty <paramref name="clientSecret"/> keeps the one in the store.</summary>
    public async Task<Result<SsoProvider>> UpdateAsync(Guid id, SsoProviderValues values, string? clientSecret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        var provider = await context.SsoProviders.SingleOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            return Error.NotFound(IdentityErrors.SsoProviderNotFound, "There is no single sign-on provider with this id.");
        }

        if (clientSecret is { Length: > ClientSecretMaxLength })
        {
            return Error.Validation(IdentityErrors.SsoInvalid, "The client secret is too long.", new FieldError("client_secret", "too_long", "Paste the client secret the provider gave you."));
        }

        var refusal = await CheckAsync(provider, values, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var before = Describe(provider.Values());
        provider.Apply(values);
        var after = Describe(provider.Values());
        var changed = after.Where(pair => !before.TryGetValue(pair.Key, out var old) || old != pair.Value).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var replaceSecret = !string.IsNullOrWhiteSpace(clientSecret);
        changed["client_secret"] = replaceSecret ? "replaced" : "kept";
        changed["changed"] = string.Join(',', changed.Keys.Where(k => k != "client_secret").Order(StringComparer.Ordinal));

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (replaceSecret)
        {
            await secrets.WriteAsync(provider.Id, clientSecret!.Trim(), cancellationToken).ConfigureAwait(false);
        }

        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.SsoProviderChanged, AuditOutcome.Success, actor.Id, provider.Name, changed, RecordType, provider.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return provider;
    }

    /// <summary>
    /// Removes a provider: its links go with it, and the roles its mapping gave; the people stay. Their sessions stay too,
    /// as a password change leaves others' sessions: the provider signs nobody in again. Its secret leaves the store last.
    /// </summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var provider = await context.SsoProviders.SingleOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            return Error.NotFound(IdentityErrors.SsoProviderNotFound, "There is no single sign-on provider with this id.");
        }

        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            var links = await context.SsoLinks.CountAsync(l => l.ProviderId == id, cancellationToken).ConfigureAwait(false);
            var roles = await provided.RemoveSourceAsync(provider.AssignmentSource, cancellationToken).ConfigureAwait(false);
            context.SsoProviders.Remove(provider);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["links_removed"] = links.ToString(CultureInfo.InvariantCulture),
                ["roles_removed"] = roles.ToString(CultureInfo.InvariantCulture),
            };
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.SsoProviderDeleted, AuditOutcome.Success, actor.Id, provider.Name, details, RecordType, provider.Id), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await secrets.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// "Test connection": reads the discovery document and the signing keys afresh and says in words what works and what
    /// does not. The client's identifier and secret can only be proved by a sign-in, and the report says so.
    /// </summary>
    public async Task<SsoTestReport> TestAsync(string discoveryUrl, CancellationToken cancellationToken)
    {
        var items = new List<SsoTestItem>();
        if (OidcDiscovery.CheckAddress(discoveryUrl, "discovery_url") is { } invalid || !discoveryUrl.EndsWith(OidcDiscovery.WellKnownPath, StringComparison.Ordinal))
        {
            items.Add(new("discovery", SsoTestStatus.Failed, "The discovery address must be an https:// address ending in /.well-known/openid-configuration."));
            return new SsoTestReport(items);
        }

        var metadata = await discovery.MetadataAsync(discoveryUrl, cancellationToken, fresh: true).ConfigureAwait(false);
        if (metadata.IsFailure)
        {
            items.Add(new("discovery", SsoTestStatus.Failed, metadata.Error.Message));
            return new SsoTestReport(items);
        }

        var found = metadata.Value;
        items.Add(new("discovery", SsoTestStatus.Passed, $"Read the discovery document of {found.Issuer}."));
        items.Add(new("endpoints", SsoTestStatus.Passed, $"Sign-in page {found.AuthorizationEndpoint}, token endpoint {found.TokenEndpoint}, keys {found.JwksUri}."));

        var keys = await discovery.KeysAsync(found, refresh: true, cancellationToken).ConfigureAwait(false);
        if (keys.IsFailure)
        {
            items.Add(new("keys", SsoTestStatus.Failed, keys.Error.Message));
        }
        else
        {
            var signing = keys.Value.GetSigningKeys().Count;
            items.Add(signing > 0
                ? new("keys", SsoTestStatus.Passed, $"Read {signing} signing key{(signing == 1 ? string.Empty : "s")}.")
                : new("keys", SsoTestStatus.Failed, "The provider publishes no signing key, so no ID token could be checked."));
        }

        items.Add(found.ResponseTypes.Count == 0 || found.ResponseTypes.Contains("code", StringComparer.Ordinal)
            ? new("code_flow", SsoTestStatus.Passed, "The provider offers the authorization code flow.")
            : new("code_flow", SsoTestStatus.Failed, "The provider does not offer the authorization code flow, which this product uses."));
        items.Add(found.CodeChallengeMethods.Contains("S256", StringComparer.Ordinal)
            ? new("pkce", SsoTestStatus.Passed, "The provider takes PKCE with S256.")
            : found.CodeChallengeMethods.Count == 0
                ? new("pkce", SsoTestStatus.Warning, "The provider does not say whether it takes PKCE with S256. Most do; the first sign-in will tell.")
                : new("pkce", SsoTestStatus.Failed, "The provider does not take PKCE with S256, which every sign-in sends."));
        var algorithms = found.SigningAlgorithms.Where(a => a.StartsWith("RS", StringComparison.Ordinal) || a.StartsWith("PS", StringComparison.Ordinal) || a.StartsWith("ES", StringComparison.Ordinal)).ToList();
        items.Add(found.SigningAlgorithms.Count == 0 || algorithms.Count > 0
            ? new("signing", SsoTestStatus.Passed, found.SigningAlgorithms.Count == 0 ? "The provider does not list its signing algorithms; the first sign-in will tell." : $"ID tokens are signed with {string.Join(", ", algorithms)}.")
            : new("signing", SsoTestStatus.Failed, $"The provider signs ID tokens only with {string.Join(", ", found.SigningAlgorithms)}; this product takes RSA or elliptic-curve signatures only."));
        items.Add(found.EndSessionEndpoint is not null
            ? new("end_session", SsoTestStatus.Passed, "People who sign out here are offered to sign out at the provider too.")
            : new("end_session", SsoTestStatus.Warning, "The provider publishes no end-session endpoint: people sign out here only."));
        items.Add(new("client", SsoTestStatus.Passed, "The client identifier and secret are proved by the first sign-in; the provider must list https://<your address>/sso/callback as a redirect URI."));
        return new SsoTestReport(items);
    }

    /// <summary>The provider's fields as audit details: everything but the secret, which is never here.</summary>
    private static Dictionary<string, string> Describe(SsoProviderValues values) => new(StringComparer.Ordinal)
    {
        ["name"] = values.Name,
        ["preset"] = values.Preset,
        ["discovery_url"] = values.DiscoveryUrl,
        ["client_id"] = values.ClientId,
        ["allowed_domains"] = string.Join(',', values.AllowedDomains),
        ["claims"] = $"{values.EmailClaim},{values.NameClaim},{values.GroupsClaim}",
        ["just_in_time"] = values.JustInTime ? "on" : "off",
        ["default_role"] = values.DefaultRoleCode ?? string.Empty,
        ["default_home_unit"] = values.DefaultHomeUnitId?.ToString() ?? string.Empty,
        ["sso_only"] = values.SsoOnly ? "on" : "off",
        ["trust_provider_mfa"] = values.TrustProviderMfa ? "on" : "off",
        ["role_mappings"] = string.Join(';', values.RoleMappings.Select(m => $"{m.Group}={m.RoleCode}@{(m.UnitId is { } unit ? (m.IncludeChildren ? $"unit+{unit}" : $"unit:{unit}") : "tenant")}")),
    };

    /// <summary>Every rule a provider's values must meet; null when they do. An escalation is audited as a denial.</summary>
    private async Task<Error?> CheckAsync(SsoProvider? existing, SsoProviderValues values, CancellationToken cancellationToken)
    {
        var fields = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(values.Name) || values.Name.Trim().Length > SsoProvider.NameMaxLength)
        {
            fields.Add(new FieldError("name", "required", "Name the provider as the sign-in page should show it, in 100 characters at most."));
        }

        if (!SsoPresets.All.Contains(values.Preset, StringComparer.Ordinal))
        {
            fields.Add(new FieldError("preset", "invalid", "Choose Microsoft Entra ID, Google Workspace, Okta or another provider."));
        }

        if (OidcDiscovery.CheckAddress(values.DiscoveryUrl.Trim(), "discovery_url") is { } address)
        {
            fields.AddRange(address.Fields);
        }
        else if (values.DiscoveryUrl.Trim().Length > SsoProvider.DiscoveryUrlMaxLength || !values.DiscoveryUrl.Trim().EndsWith(OidcDiscovery.WellKnownPath, StringComparison.Ordinal))
        {
            fields.Add(new FieldError("discovery_url", "not_discovery", "The discovery address ends in /.well-known/openid-configuration."));
        }

        if (string.IsNullOrWhiteSpace(values.ClientId) || values.ClientId.Trim().Length > SsoProvider.ClientIdMaxLength)
        {
            fields.Add(new FieldError("client_id", "required", "Paste the client identifier the provider gave you."));
        }

        var domains = values.AllowedDomains.Select(SsoProvider.NormaliseDomain).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (domains.Count == 0 || domains.Count > SsoProvider.MaxDomains)
        {
            fields.Add(new FieldError("allowed_domains", "required", "Name at least one email domain the provider may sign people in for, 50 at most."));
        }
        else if (domains.FirstOrDefault(d => !DomainPattern().IsMatch(d)) is { } wrong)
        {
            fields.Add(new FieldError("allowed_domains", "invalid", $"'{wrong}' is not a domain. Type it as example.com."));
        }
        else
        {
            var others = await context.SsoProviders.AsNoTracking().Where(p => existing == null || p.Id != existing.Id).Select(p => new { p.Name, p.AllowedDomains }).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (others.SelectMany(o => o.AllowedDomains.Select(d => (o.Name, Domain: d))).FirstOrDefault(o => domains.Contains(o.Domain, StringComparer.Ordinal)) is { Name: not null } taken)
            {
                fields.Add(new FieldError("allowed_domains", "taken", $"{taken.Name} signs people of {taken.Domain} in already; a domain belongs to one provider."));
            }
        }

        foreach (var (claim, field) in new[] { (values.EmailClaim, "email_claim"), (values.NameClaim, "name_claim"), (values.GroupsClaim, "groups_claim") })
        {
            if (string.IsNullOrWhiteSpace(claim) || claim.Trim().Length > SsoProvider.ClaimMaxLength || claim.Any(char.IsWhiteSpace))
            {
                fields.Add(new FieldError(field, "invalid", "Type the claim's name as the provider sends it, without spaces."));
            }
        }

        if (values.RoleMappings.Count > SsoProvider.MaxMappings || values.RoleMappings.Any(m => string.IsNullOrWhiteSpace(m.Group) || m.Group.Trim().Length > SsoProvider.GroupMaxLength || string.IsNullOrWhiteSpace(m.RoleCode)))
        {
            fields.Add(new FieldError("role_mappings", "invalid", "Each mapping names a group, in 200 characters at most, and a role; 100 mappings at most."));
        }

        if (values.DefaultHomeUnitId is { } homeUnit && await units.FindAsync(homeUnit, cancellationToken).ConfigureAwait(false) is not { Status: UnitStatus.Active })
        {
            fields.Add(new FieldError("default_home_unit_id", "not_found", "Choose an active unit."));
        }

        if (fields.Count > 0)
        {
            return Error.Validation(IdentityErrors.SsoInvalid, "Some of the provider's settings cannot work. Correct the fields marked.", fields);
        }

        if (await context.SsoProviders.AnyAsync(p => (existing == null || p.Id != existing.Id) && p.Name == values.Name.Trim(), cancellationToken).ConfigureAwait(false))
        {
            return new Error(ErrorKind.Conflict, IdentityErrors.SsoNameTaken, $"Another provider is named '{values.Name.Trim()}'.", [new FieldError("name", "taken", "Choose another name; the sign-in page shows them side by side.")]);
        }

        // ADR-0031 rule 1 for what the provider will grant: its default role at the home unit it gives and the units below,
        // and each mapping at its scope. Checked as the administrator saving it, whose rights the provider then carries.
        var grants = new List<(string Field, string RoleCode, RoleScope Scope)>();
        if (values.DefaultRoleCode is { Length: > 0 } defaultRole)
        {
            var home = values.DefaultHomeUnitId ?? (await units.RootAsync(cancellationToken).ConfigureAwait(false))?.Id;
            grants.Add(("default_role_code", defaultRole.Trim(), home is { } unit ? RoleScope.Unit(unit, includeChildren: true) : RoleScope.Tenant));
        }

        grants.AddRange(values.RoleMappings.Select(m => ("role_mappings", m.RoleCode.Trim(), m.UnitId is { } unit ? RoleScope.Unit(unit, m.IncludeChildren) : RoleScope.Tenant)));
        foreach (var (field, roleCode, scope) in grants)
        {
            var missing = await provided.MissingToGrantAsync(actor.Id, roleCode, scope, cancellationToken).ConfigureAwait(false);
            if (missing.IsFailure)
            {
                return new Error(ErrorKind.Validation, missing.Error.Code, missing.Error.Message, [new FieldError(field, "role", missing.Error.Message)]);
            }

            if (missing.Value.Count > 0)
            {
                var details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reason"] = "escalation",
                    ["role"] = roleCode,
                    ["scope"] = scope.ToString(),
                    ["missing"] = string.Join(',', missing.Value),
                };
                await audit.WriteAsync(new AuditEvent(existing is null ? IdentityAuditEvents.SsoProviderCreated : IdentityAuditEvents.SsoProviderChanged, AuditOutcome.Denied, actor.Id, values.Name.Trim(), details, RecordType, existing?.Id), cancellationToken).ConfigureAwait(false);
                return new Error(
                    ErrorKind.Forbidden,
                    IdentityErrors.SsoEscalation,
                    $"A provider may give only roles you may grant yourself. At {scope} you do not hold, for '{roleCode}': {string.Join(", ", missing.Value)}.",
                    [new FieldError(field, "escalation", $"You may not grant '{roleCode}' there.")]);
            }
        }

        return null;
    }

    [GeneratedRegex("^(?=.{1,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(\\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();
}
