using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Policies;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// The units that frame a reader, from the tree (ADR-0038): the home unit and the units below it, outside which a read
/// of a private record is audited; and the working unit and the units below it, which lists keep to. Lists are not
/// narrowed for a token, for the consolidated view, or while acting for someone, who works with the delegator's rights
/// in full (ADR-0037).
/// </summary>
public sealed class UnitReach(AuthorizationDbContext context, IPrincipals principals, ICurrentPrincipal current) : IUnitReach
{
    public async Task<IReadOnlySet<Guid>> HomeUnitsAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var home = (await principals.FindAsync([principalId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(principalId)?.HomeUnitId;
        return home is { } unit ? await SubtreeAsync(context, unit, cancellationToken).ConfigureAwait(false) : new HashSet<Guid>();
    }

    public async Task<IReadOnlySet<Guid>?> WorkingUnitsAsync(Guid principalId, CancellationToken cancellationToken)
    {
        if (principalId != current.Id || current.ActingForId is not null || current.AllUnits || current.WorkingUnitId is not { } working)
        {
            return null;
        }

        return await SubtreeAsync(context, working, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A unit and every unit below it, by the materialised path (ADR-0030): one query.</summary>
    public static async Task<IReadOnlySet<Guid>> SubtreeAsync(AuthorizationDbContext context, Guid unitId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var top = context.OrganisationUnits.Where(u => u.Id == unitId);
        var ids = await context.OrganisationUnits.AsNoTracking()
            .Where(u => top.Any(t => u.Path.StartsWith(t.Path)))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ids.ToHashSet();
    }
}
