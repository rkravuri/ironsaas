using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>A recovery link just opened: the code for the link, never stored, and when it stops working.</summary>
public sealed record RecoveryLink(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Setting a password, changing it, and recovery links (TF-012, TF-017, ADR-0046). Every path that sets a password
/// passes the tenant's policy and leaves the hash in the person's history. A recovery link is a one-time code valid for
/// 15 minutes: a newer one replaces it, using it consumes it and ends every session of the person, and it lifts a
/// lockout, because the person proved who they are another way. The person changes their own password with the current
/// one, and may end their other sessions in the same step.
/// </summary>
public sealed class PasswordService(
    IdentityDbContext context,
    IPasswordHasher hasher,
    PasswordPolicy policy,
    LoginThrottles throttles,
    SessionService sessions,
    SecurityEventLog securityEvents,
    IUnitOfWork unitOfWork,
    TimeProvider time,
    ILogger<PasswordService> logger)
{
    public static readonly TimeSpan RecoveryLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Sets an already checked password: the hash on the row, its age from now, and the history, of which the newest
    /// <see cref="PasswordHistoryEntry.Kept"/> stay. Saves; the caller holds the transaction.
    /// </summary>
    public async Task SetCheckedAsync(Principal principal, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var now = time.GetUtcNow();

        // A person from before the history began: their current password becomes its first entry, so it counts too.
        if (principal.PasswordHash is { } previous && !await context.PasswordHistory.AnyAsync(e => e.PrincipalId == principal.Id, cancellationToken).ConfigureAwait(false))
        {
            context.PasswordHistory.Add(new PasswordHistoryEntry(principal.Id, previous, principal.PasswordChangedAt ?? now.AddTicks(-1)));
        }

        var hash = hasher.Hash(password);
        principal.SetPassword(hash, now);
        context.PasswordHistory.Add(new PasswordHistoryEntry(principal.Id, hash, now));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var stale = await context.PasswordHistory
            .Where(e => e.PrincipalId == principal.Id)
            .OrderByDescending(e => e.SetAt)
            .Skip(PasswordHistoryEntry.Kept)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (stale.Count > 0)
        {
            context.PasswordHistory.RemoveRange(stale);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens a recovery link for the person, replacing any open one, and records it as a security event saying who
    /// issued it (<paramref name="actor"/>, with <paramref name="actorName"/> for an administrator or the operator) and why.
    /// The caller holds the transaction and has checked that the issuer may.
    /// </summary>
    public async Task<string> OpenRecoveryAsync(Principal principal, string actor, string? actorName, string? reason, IReadOnlyDictionary<string, string>? details, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var link = Open(principal);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var recorded = new Dictionary<string, string>(details ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["expires_at"] = link.ExpiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };
        await securityEvents.RecordAsync(principal, SecurityEventKinds.RecoveryLinkIssued, actor, actorName, reason, recorded, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("A recovery link was issued for principal {PrincipalId}, valid until {ExpiresAt}.", principal.Id, link.ExpiresAt);
        return link.Token;
    }

    /// <summary>
    /// Sets a new password with a recovery link. A code that is unknown, used, replaced or expired is refused; so is a
    /// person who cannot sign in. A password the policy refuses leaves the link open for another try within its 15
    /// minutes. Otherwise, in one transaction: the password, the link consumed, the lockout lifted, every session ended,
    /// and the two security events.
    /// </summary>
    public async Task<Result<Principal>> RecoverAsync(string token, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Invalid();
        }

        var hash = SecureTokens.Hash(token);
        var principal = await context.Principals.SingleOrDefaultAsync(p => p.RecoveryTokenHash == hash, cancellationToken).ConfigureAwait(false);
        if (principal is null)
        {
            return Invalid();
        }

        if (!principal.HasOpenRecovery(time.GetUtcNow()))
        {
            return Error.NotFound(IdentityErrors.RecoveryInvalid, "This recovery link has expired: a link works for 15 minutes. Ask your administrator for a new one.");
        }

        if (principal.Type != PrincipalType.User || principal.Status != PrincipalStatus.Active)
        {
            return Error.Forbidden(IdentityErrors.RecoveryNotAllowed, "This account cannot sign in, so its password cannot be set. Ask your administrator.");
        }

        var checkedPassword = await policy.CheckAsync(principal, password, cancellationToken).ConfigureAwait(false);
        if (checkedPassword.IsFailure)
        {
            return checkedPassword.Error;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        principal.ConsumeRecovery();
        await SetCheckedAsync(principal, password, cancellationToken).ConfigureAwait(false);
        var unlocked = await throttles.ClearAsync(principal.LoginId, cancellationToken).ConfigureAwait(false);
        var ended = await sessions.EndAllAsync(principal.Id, SessionEndReasons.Revoked, cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessions_ended"] = ended.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["unlocked"] = unlocked ? "true" : "false",
        };
        await securityEvents.RecordAsync(principal, SecurityEventKinds.RecoveryLinkUsed, SecurityEventActors.Self, null, null, details, cancellationToken, actorId: principal.Id).ConfigureAwait(false);
        await securityEvents.RecordAsync(principal, SecurityEventKinds.PasswordSetByRecovery, SecurityEventActors.Self, null, null, null, cancellationToken, actorId: principal.Id).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Principal {PrincipalId} set a password with a recovery link; {Sessions} session(s) ended.", principal.Id, ended);
        return principal;
    }

    /// <summary>
    /// The person changes their own password: the current one first, then the policy. A wrong current password counts
    /// against the login like a failed sign-in, and a locked login cannot change it: 403, not 401, because the session
    /// stays open (a lockout never ends sessions) and a 401 would sign the shell out. Ending the other sessions keeps
    /// <paramref name="keepSessionId"/>, the one asking. Any open recovery link is closed, since the person has just
    /// chosen a password.
    /// </summary>
    public async Task<Result> ChangeAsync(Guid principalId, Guid? keepSessionId, string currentPassword, string newPassword, bool endOtherSessions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);
        var principal = await context.Principals.SingleOrDefaultAsync(p => p.Id == principalId, cancellationToken).ConfigureAwait(false);
        if (principal is null || principal.Type != PrincipalType.User || principal.PasswordHash is null)
        {
            return Error.Forbidden(IdentityErrors.RecoveryNotAllowed, "This account has no password to change.");
        }

        if (await throttles.LockedUntilAsync(principal.LoginId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Error.Forbidden(IdentityErrors.LoginLocked, LoginService.LockedMessage);
        }

        if (!hasher.Verify(currentPassword, principal.PasswordHash))
        {
            var locked = await throttles.CountFailureAsync(principal.LoginId, cancellationToken).ConfigureAwait(false);
            if (locked is not null)
            {
                await using var lockTransaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
                var lockDetails = new Dictionary<string, string>(StringComparer.Ordinal) { ["locked_until"] = locked.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ["by"] = "password_change" };
                await securityEvents.RecordAsync(principal, SecurityEventKinds.LoginLocked, SecurityEventActors.System, null, null, lockDetails, cancellationToken).ConfigureAwait(false);
                await lockTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Error.Forbidden(IdentityErrors.LoginLocked, LoginService.LockedMessage);
            }

            return Error.Validation(IdentityErrors.PasswordCurrentWrong, "The current password is not correct.", new FieldError("current_password", "wrong", "Type your current password."));
        }

        var checkedPassword = await policy.CheckAsync(principal, newPassword, cancellationToken).ConfigureAwait(false);
        if (checkedPassword.IsFailure)
        {
            return checkedPassword.Error;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        principal.ConsumeRecovery();
        await SetCheckedAsync(principal, newPassword, cancellationToken).ConfigureAwait(false);
        var ended = endOtherSessions ? await sessions.EndOthersAsync(principal.Id, keepSessionId, SessionEndReasons.Revoked, cancellationToken).ConfigureAwait(false) : 0;
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["end_other_sessions"] = endOtherSessions ? "true" : "false",
            ["sessions_ended"] = ended.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        await securityEvents.RecordAsync(principal, SecurityEventKinds.PasswordChanged, SecurityEventActors.Self, null, null, details, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Principal {PrincipalId} changed their password; {Sessions} other session(s) ended.", principal.Id, ended);
        return Result.Success();
    }

    private RecoveryLink Open(Principal principal)
    {
        var token = SecureTokens.New();
        var expiresAt = time.GetUtcNow() + RecoveryLifetime;
        principal.OpenRecovery(SecureTokens.Hash(token), expiresAt);
        return new RecoveryLink(token, expiresAt);
    }

    private static Error Invalid() =>
        Error.NotFound(IdentityErrors.RecoveryInvalid, "This recovery link does not work: it was used already, a newer one replaced it, or it never existed. Ask your administrator for a new one.");
}
