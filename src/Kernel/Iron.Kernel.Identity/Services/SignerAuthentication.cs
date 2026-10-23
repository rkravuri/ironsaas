using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Settings.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Who signs, proved at every signature (ADR-0051), by the components rule of Part 11 section 11.200: the first signature
/// of a continuous session asks for both components, the login identifier and the password, and the second factor when
/// the person has one; later signatures in the same session ask for the password, and the second factor when the tenant
/// asks for it (<c>compliance.signing.later_second_factor</c>). A session ends after the tenant's idle limit, so a person
/// back after it signs in again and starts at the first rule. A person who signs in through their company login has no
/// password here: each signature takes a fresh sign-in at the provider, made within the step-up window. Nobody signs while
/// impersonating, while acting for someone, with a token, as a service account, or as a break-glass account. A wrong
/// password or code counts against the login exactly as at sign-in, in its own transaction.
/// </summary>
public sealed class SignerAuthentication(
    RequestPrincipal requestPrincipal,
    IdentityDbContext context,
    IPasswordHasher hasher,
    LoginThrottles throttles,
    FactorService factors,
    AuthenticationChallenges challenges,
    SsoPeople ssoPeople,
    SecurityEventLog securityEvents,
    ISettings settings,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    TimeProvider time,
    ILogger<SignerAuthentication> logger) : ISignerAuthentication
{
    public const string InvalidMessage = "That did not confirm it is you. Check your login identifier, your password and your code, and try again.";

    public async Task<Result<SignerChallenge>> ChallengeAsync(CancellationToken cancellationToken)
    {
        var signer = await SignerAsync(cancellationToken).ConfigureAwait(false);
        if (signer.IsFailure)
        {
            return signer.Error;
        }

        var (current, principal, session) = signer.Value;
        var first = session.SigningSince is null;
        if (await CompanyLoginAsync(principal, cancellationToken).ConfigureAwait(false) is { } provider)
        {
            return new SignerChallenge(first, false, false, false, [], null)
            {
                ProviderId = provider.Id,
                ProviderName = provider.Name,
                ProviderConfirmed = Fresh(session.ProviderConfirmedAt),
            };
        }

        var methods = await factors.MethodsAsync(principal.Id, cancellationToken).ConfigureAwait(false);
        var second = await SecondFactorAskedAsync(principal, first, cancellationToken).ConfigureAwait(false);
        string? options = null;
        if (second && methods.Contains(FactorKinds.Passkey, StringComparer.Ordinal))
        {
            var challenge = await challenges.StartForSessionAsync(ChallengePurposes.Reauthentication, principal.Id, current.SessionId, cancellationToken).ConfigureAwait(false);
            options = await factors.PasskeyAssertionAsync(challenge, principal.Id, cancellationToken).ConfigureAwait(false);
        }

        return new SignerChallenge(first, first, true, second, second ? methods : [], options);
    }

    public async Task<Result<SignerIdentity>> AuthenticateAsync(SigningCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var signer = await SignerAsync(cancellationToken).ConfigureAwait(false);
        if (signer.IsFailure)
        {
            return signer.Error;
        }

        var (current, principal, session) = signer.Value;
        var first = session.SigningSince is null;
        string method;
        if (await CompanyLoginAsync(principal, cancellationToken).ConfigureAwait(false) is { } provider)
        {
            // The provider's fresh sign-in, taken by this signature only: two signatures need two sign-ins.
            var confirmed = session.ProviderConfirmedAt;
            var taken = Fresh(confirmed) && await context.Sessions
                .Where(s => s.Id == session.Id && s.ProviderConfirmedAt == confirmed)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProviderConfirmedAt, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false) == 1;
            if (!taken)
            {
                return new Error(ErrorKind.Forbidden, ComplianceErrors.ProviderSignInRequired, $"Sign in again at {provider.Name} to sign: each signature asks for a fresh sign-in there.")
                {
                    Extensions = new Dictionary<string, object>(StringComparer.Ordinal) { ["provider_id"] = provider.Id, ["provider_name"] = provider.Name },
                };
            }

            method = SignatureMethods.CompanyLogin;
        }
        else
        {
            var proved = await PasswordAndFactorAsync(current, principal, credentials, first, cancellationToken).ConfigureAwait(false);
            if (proved.IsFailure)
            {
                return proved.Error;
            }

            method = proved.Value;
        }

        if (first)
        {
            var now = time.GetUtcNow();
            await context.Sessions.Where(s => s.Id == session.Id && s.SigningSince == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SigningSince, now), cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("Principal {PrincipalId} proved who they are for a signature in session {SessionId} by {Method}{First}.", principal.Id, session.Id, method, first ? ", the first of the session" : string.Empty);
        return new SignerIdentity(principal.Id, principal.DisplayName, principal.LoginId, method, session.Id);
    }

    public async Task<Result<SignerIdentity>> IdentifyAsync(CancellationToken cancellationToken)
    {
        var signer = await SignerAsync(cancellationToken).ConfigureAwait(false);
        return signer.IsFailure
            ? signer.Error
            : new SignerIdentity(signer.Value.Principal.Id, signer.Value.Principal.DisplayName, signer.Value.Principal.LoginId, SignatureMethods.Session, signer.Value.Session.Id);
    }

    /// <summary>The person signed in now, their row and their session; or why they may never sign.</summary>
    private async Task<Result<(SignedInPrincipal Current, Principal Principal, Session Session)>> SignerAsync(CancellationToken cancellationToken)
    {
        var current = requestPrincipal.Current;
        if (current is null)
        {
            return Error.Unauthorized(Iron.Kernel.Api.Permissions.PermissionErrors.NotAuthenticated, "Sign in to sign.");
        }

        if (current.Kind == AuthenticationKind.Token)
        {
            return Error.Forbidden(ComplianceErrors.Token, "A token cannot sign: a signature is a person's own, made with their own credentials in their own session.");
        }

        if (current.Type != PrincipalType.User)
        {
            return Error.Forbidden(ComplianceErrors.ServiceAccount, "A service account cannot sign: a signature is a person's own.");
        }

        if (current.ImpersonatorId is not null)
        {
            return Error.Forbidden(ComplianceErrors.Impersonation, "You are signed in as someone else, so you cannot sign: a signature is only ever its signer's own. End the sign-in as them first.");
        }

        if (current.ActingForId is not null)
        {
            return Error.Forbidden(ComplianceErrors.ActingFor, "You are acting for someone. A signature is always your own: stop acting for them, then sign.");
        }

        var principal = await context.Principals.AsNoTracking().SingleOrDefaultAsync(p => p.Id == current.Id, cancellationToken).ConfigureAwait(false);
        var session = await context.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == current.SessionId, cancellationToken).ConfigureAwait(false);
        if (principal is null || session is null)
        {
            return Error.Unauthorized(Iron.Kernel.Api.Permissions.PermissionErrors.NotAuthenticated, "Sign in to sign.");
        }

        if (principal.BreakGlassUntil is not null)
        {
            return Error.Forbidden(ComplianceErrors.BreakGlass, "A break-glass account repairs a tenant; it never signs its records.");
        }

        return (current, principal, session);
    }

    /// <summary>The company login a person without a password, or held to single sign-on, signs through; null for everyone else.</summary>
    private async Task<SsoProvider?> CompanyLoginAsync(Principal principal, CancellationToken cancellationToken)
    {
        var status = await ssoPeople.SsoOnlyAsync(principal, cancellationToken).ConfigureAwait(false);
        if (principal.PasswordHash is not null && !status.SsoOnly)
        {
            return null;
        }

        return status.Provider ?? (await ssoPeople.LinkedProvidersAsync(principal.Id, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
    }

    private bool Fresh(DateTimeOffset? confirmedAt) =>
        confirmedAt is { } at && time.GetUtcNow() - at <= SsoSignInService.StepUpWindow;

    /// <summary>The second factor at the first signature when the person has one; at a later one when the tenant asks too.</summary>
    private async Task<bool> SecondFactorAskedAsync(Principal principal, bool first, CancellationToken cancellationToken)
    {
        if (!await factors.HasAnyAsync(principal.Id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return first || await settings.GetAsync(ComplianceSettings.LaterSecondFactor, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The components the rule asks for, checked as at sign-in; the method that proved them.</summary>
    private async Task<Result<string>> PasswordAndFactorAsync(SignedInPrincipal current, Principal principal, SigningCredentials credentials, bool first, CancellationToken cancellationToken)
    {
        if (await throttles.LockedUntilAsync(principal.LoginId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Error.Forbidden(IdentityErrors.LoginLocked, LoginService.LockedMessage);
        }

        // The first signature's identification component: the person types who they are, as at sign-in.
        if (first && !string.Equals(Principal.NormaliseLoginId(credentials.LoginId), principal.LoginId, StringComparison.Ordinal))
        {
            return await WrongAsync(principal, "wrong_login_id_signature", cancellationToken).ConfigureAwait(false);
        }

        if (principal.PasswordHash is null || !hasher.Verify(credentials.Password ?? string.Empty, principal.PasswordHash))
        {
            return await WrongAsync(principal, "wrong_password_signature", cancellationToken).ConfigureAwait(false);
        }

        if (!await SecondFactorAskedAsync(principal, first, cancellationToken).ConfigureAwait(false))
        {
            return SignatureMethods.Password;
        }

        var proof = new SecondFactorProof(credentials.Code, credentials.RecoveryCode, credentials.Passkey);
        var tracked = await context.Principals.SingleAsync(p => p.Id == principal.Id, cancellationToken).ConfigureAwait(false);
        var challenge = await challenges.FindForSessionAsync(current.SessionId, ChallengePurposes.Reauthentication, cancellationToken).ConfigureAwait(false);
        Result<string> verified = proof.IsEmpty
            ? Error.Unauthorized(IdentityErrors.SecondFactorInvalid, FactorService.InvalidMessage)
            : await factors.VerifyAsync(tracked, proof, challenge, cancellationToken).ConfigureAwait(false);
        if (verified.IsFailure)
        {
            // The same codes and facts as at sign-in, as a 403: the session stays.
            return new Error(ErrorKind.Forbidden, verified.Error.Code, verified.Error.Message) { Extensions = verified.Error.Extensions };
        }

        if (challenge is not null)
        {
            await challenges.EndAsync(challenge, cancellationToken).ConfigureAwait(false);
        }

        return verified.Value switch
        {
            FactorKinds.AuthenticatorApp => SignatureMethods.PasswordAndApp,
            FactorKinds.Passkey => SignatureMethods.PasswordAndPasskey,
            _ => SignatureMethods.PasswordAndRecoveryCode,
        };
    }

    /// <summary>A wrong identifier or password: counted against the login as a failed sign-in, the lock included.</summary>
    private async Task<Error> WrongAsync(Principal principal, string reason, CancellationToken cancellationToken)
    {
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, principal.Id, principal.LoginId, Reason: reason), cancellationToken).ConfigureAwait(false);
        var locked = await throttles.CountFailureAsync(principal.LoginId, cancellationToken).ConfigureAwait(false);
        if (locked is null)
        {
            return Error.Forbidden(ComplianceErrors.Invalid, InvalidMessage);
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["locked_until"] = locked.Value.ToString("O", CultureInfo.InvariantCulture), ["by"] = "signature" };
        var tracked = await context.Principals.SingleAsync(p => p.Id == principal.Id, cancellationToken).ConfigureAwait(false);
        await securityEvents.RecordAsync(tracked, SecurityEventKinds.LoginLocked, SecurityEventActors.System, null, null, details, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Error.Forbidden(IdentityErrors.LoginLocked, LoginService.LockedMessage);
    }
}
