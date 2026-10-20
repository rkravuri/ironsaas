using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// Who may review a unit's access (ADR-0037): the principals holding <c>authz.reviews.decide</c> there, tenant-wide, at the
/// unit, or with children at the unit or a unit above it. The escalation names them, and an item nobody but its own
/// person may review goes to the campaign's owner.
/// </summary>
public sealed class UnitReviewers(AuthorizationDbContext context)
{
    /// <summary>The reviewers of the unit whose path from the root, the unit last, is <paramref name="unitPath"/>.</summary>
    public async Task<IReadOnlyList<Guid>> OfUnitAsync(IReadOnlyList<Guid> unitPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitPath);
        var unit = unitPath[^1];
        var path = unitPath.ToArray();
        var deciding = context.Roles.Where(r => r.Permissions.Contains(AuthorizationPermissions.ReviewsDecide.Name)).Select(r => r.Id);
        return await context.RoleAssignments.AsNoTracking()
            .Where(a => deciding.Contains(a.RoleId)
                && (a.ScopeKind == Scopes.Tenant
                    || (a.ScopeKind == Scopes.Unit && a.ScopeId == unit)
                    || (a.ScopeKind == Scopes.UnitWithChildren && path.Contains(a.ScopeId!.Value))))
            .Select(a => a.PrincipalId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
