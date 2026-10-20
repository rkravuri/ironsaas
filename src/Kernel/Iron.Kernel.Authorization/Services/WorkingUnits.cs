using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// The units a person may work for (TF-222, ADR-0038), from their own assignments, never from a delegator's: the home
/// unit, each unit an assignment names (a unit alone or with its children), and the root for a tenant-wide one.
/// Inactive units are left out. "All my units" is offered when there is more than one unit to choose and an assignment
/// reaches several units at once: a parent with children below it, or the whole tenant (TF-222 AC1).
/// </summary>
public sealed class WorkingUnits(AuthorizationDbContext context) : IWorkingUnits
{
    public async Task<WorkingUnitOptions> OptionsAsync(Guid principalId, Guid? homeUnitId, CancellationToken cancellationToken)
    {
        var assignments = await context.RoleAssignments.AsNoTracking()
            .Where(a => a.PrincipalId == principalId && a.ScopeKind != Scopes.Record)
            .Select(a => new { a.ScopeKind, a.ScopeId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var tenantWide = assignments.Any(a => a.ScopeKind == Scopes.Tenant);
        var named = assignments.Where(a => a.ScopeId is not null).Select(a => a.ScopeId!.Value).ToHashSet();
        if (homeUnitId is { } home)
        {
            named.Add(home);
        }

        var ids = named.ToArray();
        var units = await context.OrganisationUnits.AsNoTracking()
            .Where(u => (ids.Contains(u.Id) || (tenantWide && u.ParentId == null)) && u.Status == UnitStatus.Active)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var pathIds = units.SelectMany(u => OrganisationUnit.PathIds(u.Path)).Distinct().ToArray();
        var names = await context.OrganisationUnits.AsNoTracking().Where(u => pathIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken).ConfigureAwait(false);
        var choices = OrganisationUnitService.InTreeOrder(units)
            .Select(u => new WorkingUnitChoice(u.Id, u.Name, u.Code, u.Depth, [.. OrganisationUnit.PathIds(u.Path).Select(id => names.GetValueOrDefault(id, string.Empty))]))
            .ToList();

        var withChildren = assignments.Where(a => a.ScopeKind == Scopes.UnitWithChildren).Select(a => a.ScopeId!.Value).ToArray();
        var reachesSeveral = tenantWide || await context.OrganisationUnits.AnyAsync(u => u.ParentId != null && withChildren.Contains(u.ParentId.Value), cancellationToken).ConfigureAwait(false);
        return new WorkingUnitOptions(choices, choices.Count > 1 && reachesSeveral);
    }
}
