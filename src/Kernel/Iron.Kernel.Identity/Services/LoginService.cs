using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// A sign-in that succeeded: the session and its cookie token. When the password is past the tenant's maximum age there
/// is no session; <see cref="RecoveryToken"/> is the code of a recovery link the person uses to set a new one (TF-012).
/// </summary>
public sealed record LoginOutcome(Principal Principal, Session? Session, string? Token, string? RecoveryToken = null)
{
    public bool PasswordExpired => RecoveryToken is not null;
}

/// <summary>
/// Password login (TF-011). Every failure gets the same answer, so nothing reveals whether the account exists, and
/// every failure is counted and audited with its real reason for the administrators. Failures also count against the
/// login identifier, known or not, and lock it at the tenant's threshold (TF-012, ADR-0046): while it is locked even the
/// correct password is refused, with an answer that is the same for every identifier. A success writes the session row,
/// the login stamp and the audit event in one transaction.
/// </summary>
public sealed class LoginService(
    IdentityDbContext context,
    IPasswordHasher hasher,
    SessionService sessions,
    LoginThrottles throttles,
    PasswordPolicy policy,
    PasswordService passwords,
    SecurityEventLog securityEvents,
    IUnitOfWork unitOfWork,
    TimeProvider time,
    IAuditWriter audit,
    IHttpContextAccessor accessor,
    ILogger<LoginService> logger)
{
    public const string FailureMessage = "The login details are not correct.";

    public const string LockedMessage = "Too many failed sign-ins for this login. Try again later, or ask your administrator to unlock it.";

    public const string ExpiredMessage = "Your password has expired. Choose a new one to sign in.";

    public async Task<Result<LoginOutcome>> LoginAsync(string loginId, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        var normalised = Principal.NormaliseLoginId(loginId);

        // Locked or not is known before the account is looked up, and costs the same verification, so the answer reveals
        // nothing about the account behind the identifier.
        if (await throttles.LockedUntilAsync(normalised, cancellationToken).ConfigureAwait(false) is not null)
        {
            hasher.VerifyOrDecoy(password, null);
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, Guid.Empty, normalised, Reason: "locked"), cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Login refused for {LoginId}: the identifier is locked.", normalised);
            return Error.Unauthorized(IdentityErrors.LoginLocked, LockedMessage);
        }

        var principal = normalised.Length == 0 ? null : await context.Principals.SingleOrDefaultAsync(p => p.LoginId == normalised, cancellationToken).ConfigureAwait(false);

        if (principal is null)
        {
            hasher.VerifyOrDecoy(password, null);
            return await FailAsync(null, normalised, "unknown_login", cancellationToken).ConfigureAwait(false);
        }

        if (principal.Type != PrincipalType.User)
        {
            hasher.VerifyOrDecoy(password, principal.PasswordHash);
            return await FailAsync(principal, normalised, "not_a_user", cancellationToken).ConfigureAwait(false);
        }

        if (!principal.CanLogIn)
        {
            hasher.VerifyOrDecoy(password, principal.PasswordHash);
            return await FailAsync(principal, normalised, principal.Status.ToString().ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        }

        if (!hasher.Verify(password, principal.PasswordHash!))
        {
            return await FailAsync(principal, normalised, "wrong_password", cancellationToken).ConfigureAwait(false);
        }

        var rules = await policy.RulesAsync(cancellationToken).ConfigureAwait(false);
        if (principal.PasswordExpired(time.GetUtcNow(), rules.MaximumAgeDays))
        {
            return await ExpiredAsync(principal, rules, cancellationToken).ConfigureAwait(false);
        }

        // Another attempt on the same account may save its stamp between our read and our write (the version column
        // is the concurrency token). That must never turn a correct login into an error: take the newer row and go again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
                principal.RecordLogin(time.GetUtcNow());
                await throttles.ClearAsync(normalised, cancellationToken).ConfigureAwait(false);
                var (session, token) = await sessions.StartAsync(principal, cancellationToken, created =>
                {
                    // From here on the request acts as the user: the session row and the audit event carry it as created_by.
                    if (accessor.HttpContext is { } http)
                    {
                        RequestPrincipal.Set(http, sessions.Describe(principal, created));
                    }
                }).ConfigureAwait(false);
                await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginSucceeded, AuditOutcome.Success, principal.Id, principal.LoginId, RecordType: "session", RecordId: session.Id), cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                logger.LogInformation("Login succeeded for principal {PrincipalId}.", principal.Id);
                return new LoginOutcome(principal, session, token);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                logger.LogInformation("Login for principal {PrincipalId} raced another attempt; retrying ({Attempt}).", principal.Id, attempt);
                await ReloadAsync(principal, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private const int MaxAttempts = 3;

    /// <summary>
    /// The password is right but past its maximum age: no session, and a recovery link for the person to set a new one,
    /// issued and recorded like any other (the person proved they hold the password, so the link is theirs).
    /// </summary>
    private async Task<Result<LoginOutcome>> ExpiredAsync(Principal principal, PasswordRules rules, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["maximum_age_days"] = rules.MaximumAgeDays.ToString(CultureInfo.InvariantCulture) };
        var token = await passwords.OpenRecoveryAsync(principal, SecurityEventActors.System, null, "password expired", details, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, principal.Id, principal.LoginId, Reason: "password_expired"), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Login of principal {PrincipalId} found the password expired; a recovery link was issued.", principal.Id);
        return new LoginOutcome(principal, null, null, token);
    }

    /// <summary>Takes the row as it is now and forgets the session this attempt tried to add, so the next attempt starts clean.</summary>
    private async Task ReloadAsync(Principal principal, CancellationToken cancellationToken)
    {
        await context.Entry(principal).ReloadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in context.ChangeTracker.Entries<Session>().Where(e => e.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task<Result<LoginOutcome>> FailAsync(Principal? principal, string loginId, string reason, CancellationToken cancellationToken)
    {
        if (principal is not null)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    principal.RecordFailedLogin(time.GetUtcNow());
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
                {
                    await ReloadAsync(principal, cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateConcurrencyException exception)
                {
                    // The count is bookkeeping; the answer and the audit event matter more than the exact number.
                    logger.LogWarning(exception, "The failed-login count of principal {PrincipalId} was not updated after {Attempts} attempts.", principal.Id, MaxAttempts);
                    break;
                }
            }
        }

        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, principal?.Id ?? Guid.Empty, loginId, Reason: reason), cancellationToken).ConfigureAwait(false);
        logger.LogWarning("Login failed for {LoginId}: {Reason}.", loginId, reason);
        var lockedUntil = await throttles.CountFailureAsync(loginId, cancellationToken).ConfigureAwait(false);
        if (lockedUntil is null)
        {
            return Error.Unauthorized(IdentityErrors.LoginInvalid, FailureMessage);
        }

        await RecordLockAsync(principal, loginId, lockedUntil.Value, cancellationToken).ConfigureAwait(false);
        return Error.Unauthorized(IdentityErrors.LoginLocked, LockedMessage);
    }

    /// <summary>
    /// The failure that locked the identifier: an audit event for the administrators, and for a person who exists a
    /// security event they see in My profile. An identifier nobody has gets the audit event alone.
    /// </summary>
    private async Task RecordLockAsync(Principal? principal, string loginId, DateTimeOffset lockedUntil, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["locked_until"] = lockedUntil.ToString("O", CultureInfo.InvariantCulture) };
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        if (principal is null)
        {
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginLocked, AuditOutcome.Success, Guid.Empty, loginId, details), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await securityEvents.RecordAsync(principal, SecurityEventKinds.LoginLocked, SecurityEventActors.System, null, null, details, cancellationToken, actorId: principal.Id).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogWarning("Login identifier {LoginId} locked until {LockedUntil}.", loginId, lockedUntil);
    }
}
