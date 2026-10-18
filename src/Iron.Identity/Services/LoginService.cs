using Iron.Audit.Contracts;
using Iron.Core.Results;
using Iron.Data.Transactions;
using Iron.Identity.Contracts;
using Iron.Identity.Data;
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

    private async Task<Result<LoginOutcome>> FailAsync(Principal? principal, string loginId, string reason, CancellationToken cancellationToken)
    {
        if (principal is not null)
        {
            principal.RecordFailedLogin(time.GetUtcNow());
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, principal?.Id ?? Guid.Empty, loginId, Reason: reason), cancellationToken).ConfigureAwait(false);
        logger.LogWarning("Login failed for {LoginId}: {Reason}.", loginId, reason);
        return Error.Unauthorized(IdentityErrors.LoginInvalid, FailureMessage);
    }
}
