using Iron.Audit.Contracts;
using Iron.Data.Transactions;
using Iron.Identity.Contracts;
using Iron.Identity.Data;
using Iron.Kernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Identity.Services;

public sealed record LoginOutcome(Principal Principal, Session Session, string Token);

/// <summary>
/// Password login (TF-011). Every failure gets the same answer, so nothing reveals whether the account exists, and
/// every failure is counted and audited with its real reason for the administrators. A success writes the session row,
/// the login stamp and the audit event in one transaction.
/// </summary>
public sealed class LoginService(
    IdentityDbContext context,
    IPasswordHasher hasher,
    SessionService sessions,
    IUnitOfWork unitOfWork,
    TimeProvider time,
    IAuditWriter audit,
    IHttpContextAccessor accessor,
    ILogger<LoginService> logger)
{
    public const string FailureMessage = "The login details are not correct.";


    public async Task<Result<LoginOutcome>> LoginAsync(string loginId, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        var normalised = Principal.NormaliseLoginId(loginId);
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

        // Another attempt on the same account may save its stamp between our read and our write (the version column
        // is the concurrency token). That must never turn a correct login into an error: take the newer row and go again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
                principal.RecordLogin(time.GetUtcNow());
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
        return Error.Unauthorized(IdentityErrors.LoginInvalid, FailureMessage);
    }
}
