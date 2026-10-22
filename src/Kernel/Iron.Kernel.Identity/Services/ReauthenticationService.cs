using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>What confirming it is them asks of a person: the password always, and the second factor when they have one.</summary>
public sealed record ReauthenticationChallenge(bool SecondFactor, IReadOnlyList<string> Methods, string? PasskeyOptions);

/// <summary>
/// Step-up re-authentication (TF-248, ADR-0047): a signed-in person confirms it is them before a high-risk action, with
/// their password and, when they have one, their second factor; the session's authentication time moves to now, and the
/// action goes on. Every refusal is 403, never 401, because the session stays open and a 401 would sign the shell out. A
/// wrong password counts against the login as a failed sign-in does, and a wrong second factor as at sign-in, cooling
/// and lock included. A token cannot re-authenticate: its client asks for a new one with its credentials.
/// </summary>
public sealed class ReauthenticationService(
    IdentityDbContext context,
    IPasswordHasher hasher,
    LoginThrottles throttles,
    FactorService factors,
    AuthenticationChallenges challenges,
    SessionService sessions,
    SecurityEventLog securityEvents,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ILogger<ReauthenticationService> logger)
{
    public const string InvalidMessage = "That did not confirm it is you. Check your password and your code, and try again.";

    public async Task<Result<ReauthenticationChallenge>> ChallengeAsync(SignedInPrincipal current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Kind == AuthenticationKind.Token)
        {
            return TokenRefusal();
        }

        var methods = await factors.MethodsAsync(current.Id, cancellationToken).ConfigureAwait(false);
        string? options = null;
        if (methods.Contains(FactorKinds.Passkey, StringComparer.Ordinal))
        {
            var challenge = await challenges.StartForSessionAsync(ChallengePurposes.Reauthentication, current.Id, current.SessionId, cancellationToken).ConfigureAwait(false);
            options = await factors.PasskeyAssertionAsync(challenge, current.Id, cancellationToken).ConfigureAwait(false);
        }

        return new ReauthenticationChallenge(methods.Count > 0, methods, options);
    }

    /// <summary>Checks the password, then the second factor; when both prove the person, high-risk actions may go on for a few minutes.</summary>
    public async Task<Result<DateTimeOffset>> ReauthenticateAsync(SignedInPrincipal current, string password, SecondFactorProof proof, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proof);
        if (current.Kind == AuthenticationKind.Token)
        {
            return TokenRefusal();
        }

        var principal = await context.Principals.SingleOrDefaultAsync(p => p.Id == current.Id, cancellationToken).ConfigureAwait(false);
        if (principal?.PasswordHash is null)
        {
            return Error.Forbidden(IdentityErrors.ReauthenticationInvalid, InvalidMessage);
        }

        if (await throttles.LockedUntilAsync(principal.LoginId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Error.Forbidden(IdentityErrors.LoginLocked, LoginService.LockedMessage);
        }

        if (!hasher.Verify(password ?? string.Empty, principal.PasswordHash))
        {
            return await WrongPasswordAsync(principal, cancellationToken).ConfigureAwait(false);
        }

        string? secondFactor = null;
        if (await factors.HasAnyAsync(principal.Id, cancellationToken).ConfigureAwait(false))
        {
            var challenge = await challenges.FindForSessionAsync(current.SessionId, ChallengePurposes.Reauthentication, cancellationToken).ConfigureAwait(false);
            Result<string> verified = proof.IsEmpty
                ? Error.Unauthorized(IdentityErrors.SecondFactorInvalid, FactorService.InvalidMessage)
                : await factors.VerifyAsync(principal, proof, challenge, cancellationToken).ConfigureAwait(false);
            if (verified.IsFailure)
            {
                // The same codes and facts as at sign-in, as a 403: the session stays.
                return new Error(ErrorKind.Forbidden, verified.Error.Code, verified.Error.Message) { Extensions = verified.Error.Extensions };
            }

            secondFactor = verified.Value;
            if (challenge is not null)
            {
                await challenges.EndAsync(challenge, cancellationToken).ConfigureAwait(false);
            }
        }

        var at = await sessions.ReauthenticatedAsync(current.SessionId, cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["second_factor"] = secondFactor ?? "none" };
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.Reauthenticated, AuditOutcome.Success, principal.Id, principal.LoginId, details, "session", current.SessionId), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Principal {PrincipalId} re-authenticated in session {SessionId}.", principal.Id, current.SessionId);
        return at;
    }

    private async Task<Error> WrongPasswordAsync(Principal principal, CancellationToken cancellationToken)
    {
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, principal.Id, principal.LoginId, Reason: "wrong_password_reauthentication"), cancellationToken).ConfigureAwait(false);
        var locked = await throttles.CountFailureAsync(principal.LoginId, cancellationToken).ConfigureAwait(false);
        if (locked is null)
        {
            return Error.Forbidden(IdentityErrors.ReauthenticationInvalid, InvalidMessage);
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["locked_until"] = locked.Value.ToString("O", CultureInfo.InvariantCulture), ["by"] = "reauthentication" };
        await securityEvents.RecordAsync(principal, SecurityEventKinds.LoginLocked, SecurityEventActors.System, null, null, details, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Error.Forbidden(IdentityErrors.LoginLocked, LoginService.LockedMessage);
    }

    private static Error TokenRefusal() =>
        Error.Forbidden(IdentityErrors.ReauthenticationToken, "A token cannot confirm it again. Ask for a new token with the client's credentials; a new token counts as a recent sign-in.");
}
