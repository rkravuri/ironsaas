using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

/// <summary>Who holds the tenant administrator role tenant-wide, under the same lock as the removal guard (A2, T2).</summary>
public sealed class TenantAdministrators(AuthorizationDbContext context, IUnitOfWork unitOfWork) : ITenantAdministrators
{
    public async Task<IReadOnlyList<Guid>> LockAndListAsync(CancellationToken cancellationToken)
    {
        if (!unitOfWork.IsActive)
        {
            // A transaction-scoped lock outside a transaction is released at once and guards nothing.
            throw new InvalidOperationException("The administrators' lock needs an open unit of work.");
        }

        await context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({AssignmentService.AdministratorsLockKey})", cancellationToken).ConfigureAwait(false);
        return await Holders().ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsTenantAdministratorAsync(Guid principalId, CancellationToken cancellationToken) =>
        await Holders().AnyAsync(id => id == principalId, cancellationToken).ConfigureAwait(false);

    private IQueryable<Guid> Holders() =>
        context.RoleAssignments.AsNoTracking()
            .Where(a => a.ScopeKind == Scopes.Tenant)
            .Join(context.Roles.Where(r => r.Code == SystemRoles.TenantAdministrator), a => a.RoleId, r => r.Id, (a, _) => a.PrincipalId)
            .Distinct();
}
