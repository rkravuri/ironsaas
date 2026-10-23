using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Makes a platform client's service account (ADR-0050), as the seed makes its own: the secret hashed, the role for the
/// whole tenant, a changed secret audited. The fleet calls it for each tenant unit's client in the control plane.
/// </summary>
public sealed class ServiceAccounts(
    IdentityDbContext context,
    IPasswordHasher hasher,
    IRoleAssignments roleAssignments,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ILogger<ServiceAccounts> logger) : IServiceAccounts
{
    public async Task<Result<Guid>> EnsureAsync(string clientId, string displayName, string roleCode, string secret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var login = Principal.NormaliseLoginId(clientId);
        if (login.Length == 0)
        {
            return Error.Validation("identity.service_account.client_id", "A service account needs a client id.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var account = await context.Principals.SingleOrDefaultAsync(p => p.LoginId == login, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            account = new Principal(login, PrincipalType.ServiceAccount, displayName, null);
            account.AcceptInvitation(hasher.Hash(secret), null);
            context.Principals.Add(account);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.UserCreated, AuditOutcome.Success, account.Id, login, RecordType: "principal", RecordId: account.Id, Reason: "a platform client"), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Created the service account {ClientId} ({PrincipalId}).", login, account.Id);
        }
        else if (account.Type != PrincipalType.ServiceAccount)
        {
            return Error.Conflict("identity.service_account.not_a_client", $"{login} is a {account.Type}, not a service account.");
        }
        else if (account.PasswordHash is null || !hasher.Verify(secret, account.PasswordHash))
        {
            account.SetPassword(hasher.Hash(secret));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.ServiceAccountSecretChanged, AuditOutcome.Success, account.Id, login, RecordType: "principal", RecordId: account.Id, Reason: "a platform client"), cancellationToken).ConfigureAwait(false);
        }

        var assigned = await roleAssignments.AssignAsync(account.Id, roleCode, cancellationToken).ConfigureAwait(false);
        if (assigned.IsFailure)
        {
            return assigned.Error;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return account.Id;
    }
}
