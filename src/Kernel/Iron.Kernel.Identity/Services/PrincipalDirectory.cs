using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Answers Authorization's question about principals (ADR-0031): their type, name and home unit. A service account
/// and an API key holder are both service principals to a role.
/// </summary>
public sealed class PrincipalDirectory(IdentityDbContext context) : IPrincipals
{
    public async Task<IReadOnlyDictionary<Guid, PrincipalSummary>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.Distinct().ToArray();
        var rows = await context.Principals.AsNoTracking()
            .Where(p => wanted.Contains(p.Id))
            .Select(p => new { p.Id, p.Type, p.DisplayName, p.HomeUnitId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(
            p => p.Id,
            p => new PrincipalSummary(p.Id, p.Type == PrincipalType.User ? PrincipalKind.User : PrincipalKind.Service, p.DisplayName, p.HomeUnitId));
    }
}
