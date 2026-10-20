using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Identity's part of a merge (TF-223, ADR-0038): every principal homed at the unit that goes is homed at the unit that
/// stays, people and service accounts alike, so no one is left with an inactive home unit.
/// </summary>
public sealed class HomeUnitMerge(IdentityDbContext context) : IUnitMergeParticipant
{
    public async Task<IReadOnlyList<UnitMergePart>> PreviewAsync(Guid fromUnitId, Guid intoUnitId, CancellationToken cancellationToken)
    {
        var homed = await context.Principals.AsNoTracking().Where(p => p.HomeUnitId == fromUnitId).Select(p => new { p.Id, p.DisplayName }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [Part(homed.Select(p => (p.Id, p.DisplayName)))];
    }

    public async Task<IReadOnlyList<UnitMergePart>> ApplyAsync(Guid fromUnitId, Guid intoUnitId, CancellationToken cancellationToken)
    {
        var homed = await context.Principals.Where(p => p.HomeUnitId == fromUnitId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var principal in homed)
        {
            principal.SetHomeUnit(intoUnitId);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return [Part(homed.Select(p => (p.Id, p.DisplayName)))];
    }

    private static UnitMergePart Part(IEnumerable<(Guid Id, string Name)> people)
    {
        var ordered = people.OrderBy(p => p.Name, StringComparer.CurrentCulture).ToList();
        return new UnitMergePart(UnitMergeParts.People, "identity", null, [.. ordered.Select(p => p.Name)], [.. ordered.Select(p => p.Id.ToString("D"))], []);
    }
}
