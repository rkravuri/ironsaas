using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

public sealed class RoleAssignments(
    AuthorizationDbContext context,
    AccessGrants grants,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor) : IRoleAssignments
{
    public Task<Result> AssignAsync(Guid principalId, string roleName, CancellationToken cancellationToken) =>
        AssignAsync(principalId, roleName, RoleScope.Tenant, cancellationToken);

    public async Task<Result> AssignAsync(Guid principalId, string roleName, RoleScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (principalId == Guid.Empty)
        {
            return Error.Validation(AuthorizationErrors.PrincipalRequired, "A principal is required.");
        }

        var role = await context.Roles.SingleOrDefaultAsync(r => r.Name == roleName, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Error.NotFound(AuthorizationErrors.RoleNotFound, $"There is no role named '{roleName}'.");
        }

        if (scope.UnitId is { } unitId)
        {
            var unit = await context.OrganisationUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Id == unitId, cancellationToken).ConfigureAwait(false);
            if (unit is null)
            {
                return Error.Validation(AuthorizationErrors.UnitNotFound, "There is no organisation unit with this id; a role can only be assigned to a unit that exists.", new FieldError("unit_id", "not_found", "Choose an existing unit."));
            }

            if (!unit.IsActive)
            {
                return Error.Validation(AuthorizationErrors.UnitInactive, $"The unit '{unit.Name}' is deactivated; a role cannot be assigned to it.", new FieldError("unit_id", "inactive", "Choose an active unit."));
            }
        }

        var kind = Scopes.NameOf(scope.Kind);
        var scopeId = scope.UnitId ?? scope.RecordId;
        var exists = await context.RoleAssignments
            .AnyAsync(a => a.PrincipalId == principalId && a.RoleId == role.Id && a.ScopeKind == kind && a.ScopeId == scopeId && a.ScopeRecordType == scope.RecordType, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            var assignment = new RoleAssignment(role.Id, principalId, scope);
            await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
            context.RoleAssignments.Add(assignment);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["role"] = role.Name, ["principal_id"] = principalId.ToString(), ["scope"] = scope.ToString() };
            await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.RoleAssigned, AuditOutcome.Success, actor.Id, role.Name, details, "role_assignment", assignment.Id), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        grants.Forget(principalId); // the grant shows on the next request, not after the cache window
        return Result.Success();
    }

    public async Task<IReadOnlyList<string>> PermissionsOfAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var principal = await grants.OfAsync(principalId, cancellationToken).ConfigureAwait(false);
        return principal.TenantWide.Order(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<string>> PermissionsOfAsync(Guid principalId, AccessScope scope, CancellationToken cancellationToken)
    {
        var permissions = await grants.PermissionsAtAsync(principalId, scope, cancellationToken).ConfigureAwait(false);
        return permissions.Order(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<string>> RoleNamesOfAsync(Guid principalId, CancellationToken cancellationToken)
    {
        return await context.RoleAssignments.AsNoTracking()
            .Where(a => a.PrincipalId == principalId && a.ScopeKind == Scopes.Tenant)
            .Join(context.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => r.Name)
            .OrderBy(name => name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>What one permission reaches in a list (ADR-0030), for the modules that list business records.</summary>
public sealed class RecordAccessService(AccessGrants grants) : IRecordAccess
{
    public Task<RecordAccess> AccessibleAsync(Guid principalId, Permission permission, string recordType, CancellationToken cancellationToken) =>
        grants.AccessibleAsync(principalId, permission, recordType, cancellationToken);
}

/// <summary>Answers Iron.Kernel.Data's question on save: which of these units may not own records (TF-034).</summary>
public sealed class OrganisationUnitCheck(AuthorizationDbContext context) : IOrganisationUnitCheck
{
    public async Task<IReadOnlyList<Guid>> NotActiveAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitIds);
        var ids = unitIds.Distinct().ToArray();
        var active = await context.OrganisationUnits.AsNoTracking()
            .Where(unit => ids.Contains(unit.Id) && unit.Status == UnitStatus.Active)
            .Select(unit => unit.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ids.Except(active).ToList();
    }
}
