using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Iron.Kernel.Authorization.Services;

/// <summary>One assignment as the evaluator needs it: where it applies and what its role grants.</summary>
public sealed record Grant(RoleScopeKind Kind, Guid? ScopeId, string? RecordType, IReadOnlyCollection<string> Permissions);

/// <summary>Every assignment of one principal, with the tenant-wide permissions gathered once.</summary>
public sealed class PrincipalGrants
{
    public PrincipalGrants(IReadOnlyList<Grant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        Grants = grants;
        TenantWide = grants.Where(g => g.Kind == RoleScopeKind.Tenant).SelectMany(g => g.Permissions).ToHashSet(StringComparer.Ordinal);
        HasUnitGrants = grants.Any(g => g.Kind is RoleScopeKind.Unit or RoleScopeKind.UnitWithChildren);
    }

    public IReadOnlyList<Grant> Grants { get; }

    public IReadOnlySet<string> TenantWide { get; }

    public bool HasUnitGrants { get; }

    /// <summary>
    /// The pure rule (ADR-0030): tenant-wide grants apply everywhere; a unit grant applies to its unit; a grant with
    /// children applies to its unit and every unit below it; a record grant applies to its record. <paramref name="unitPath"/>
    /// is the ids from the root to the scope's unit, or empty when the scope has no unit or the unit is unknown.
    /// </summary>
    public HashSet<string> PermissionsAt(AccessScope scope, IReadOnlyList<Guid> unitPath)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(unitPath);
        var result = new HashSet<string>(TenantWide, StringComparer.Ordinal);
        if (scope.Kind == AccessScopeKind.Tenant)
        {
            return result;
        }

        var unit = unitPath.Count > 0 ? unitPath[^1] : (Guid?)null;
        foreach (var grant in Grants)
        {
            var applies = grant.Kind switch
            {
                RoleScopeKind.Unit => unit is not null && grant.ScopeId == unit,
                RoleScopeKind.UnitWithChildren => grant.ScopeId is { } top && unitPath.Contains(top),
                RoleScopeKind.Record => scope.Kind == AccessScopeKind.Record && grant.ScopeId == scope.RecordId && string.Equals(grant.RecordType, scope.RecordType, StringComparison.Ordinal),
                _ => false,
            };
            if (applies)
            {
                result.UnionWith(grant.Permissions);
            }
        }

        return result;
    }
}

/// <summary>
/// Loads a principal's assignments with their roles' permissions, caches them for at most 60 seconds (ADR-0012), and
/// answers for a scope or for a list (ADR-0030). A record or unit check reads one unit's path by its key; a list check
/// runs one query over the units, whatever the list's length.
/// </summary>
public sealed class AccessGrants(AuthorizationDbContext context, IMemoryCache cache, PermissionCacheOptions options, TimeProvider time)
{
    /// <summary>Entries are kept in the memory cache for a while; the window itself is checked against Foundation's clock.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public static string CacheKey(Guid principalId) => $"authz:grants:{principalId:N}";

    public async Task<PrincipalGrants> OfAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var key = CacheKey(principalId);
        var now = time.GetUtcNow();
        if (cache.TryGetValue(key, out CachedGrants? cached) && cached is not null && cached.ExpiresAt > now)
        {
            return cached.Grants;
        }

        var rows = await context.RoleAssignments.AsNoTracking()
            .Where(assignment => assignment.PrincipalId == principalId)
            .Join(context.Roles.AsNoTracking(), assignment => assignment.RoleId, role => role.Id, (assignment, role) => new { assignment.ScopeKind, assignment.ScopeId, assignment.ScopeRecordType, role.Permissions })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var grants = new PrincipalGrants(rows.Select(row => new Grant(Scopes.KindOf(row.ScopeKind), row.ScopeId, row.ScopeRecordType, row.Permissions)).ToList());
        var seconds = Math.Clamp(options.CacheSeconds, 0, PermissionCacheOptions.MaxCacheSeconds);
        if (seconds > 0)
        {
            cache.Set(key, new CachedGrants(grants, now.AddSeconds(seconds)), Retention);
        }

        return grants;
    }

    public void Forget(Guid principalId) => cache.Remove(CacheKey(principalId));

    /// <summary>The permissions a principal holds at a scope. Reads one unit's path when the principal has unit grants at all.</summary>
    public async Task<IReadOnlySet<string>> PermissionsAtAsync(Guid principalId, AccessScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var grants = await OfAsync(principalId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Guid> unitPath = [];
        if (scope.UnitId is { } unitId && grants.HasUnitGrants)
        {
            var path = await context.OrganisationUnits.AsNoTracking()
                .Where(unit => unit.Id == unitId)
                .Select(unit => unit.Path)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            unitPath = path is null ? [] : OrganisationUnit.PathIds(path);
        }

        return grants.PermissionsAt(scope, unitPath);
    }

    /// <summary>
    /// What one permission reaches in a list of records of a type (ADR-0030): everything for a tenant-wide grant;
    /// otherwise the units named by unit grants, every unit below a unit named by a grant with children (one query),
    /// and the records granted one by one.
    /// </summary>
    public async Task<RecordAccess> AccessibleAsync(Guid principalId, Permission permission, string recordType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        var grants = await OfAsync(principalId, cancellationToken).ConfigureAwait(false);
        if (grants.TenantWide.Contains(permission.Name))
        {
            return RecordAccess.Everything;
        }

        var holding = grants.Grants.Where(g => g.Permissions.Contains(permission.Name, StringComparer.Ordinal)).ToList();
        var records = holding.Where(g => g.Kind == RoleScopeKind.Record && string.Equals(g.RecordType, recordType, StringComparison.Ordinal)).Select(g => g.ScopeId!.Value).ToList();
        var exact = holding.Where(g => g.Kind == RoleScopeKind.Unit).Select(g => g.ScopeId!.Value).ToArray();
        var tops = holding.Where(g => g.Kind == RoleScopeKind.UnitWithChildren).Select(g => g.ScopeId!.Value).ToArray();
        if (exact.Length == 0 && tops.Length == 0)
        {
            return RecordAccess.Within([], records);
        }

        var subtreeTops = context.OrganisationUnits.Where(top => tops.Contains(top.Id));
        var units = await context.OrganisationUnits.AsNoTracking()
            .Where(unit => exact.Contains(unit.Id) || subtreeTops.Any(top => unit.Path.StartsWith(top.Path)))
            .Select(unit => unit.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return RecordAccess.Within(units, records);
    }

    private sealed record CachedGrants(PrincipalGrants Grants, DateTimeOffset ExpiresAt);
}
