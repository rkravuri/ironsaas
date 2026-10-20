using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// One assignment as the evaluator needs it: where it applies and what its role grants, with the assignment and the role
/// it comes from, which an explanation names (ADR-0039).
/// </summary>
public sealed record Grant(RoleScopeKind Kind, Guid? ScopeId, string? RecordType, IReadOnlyCollection<string> Permissions, Guid? AssignmentId = null, Guid? RoleId = null, string? RoleCode = null, string? RoleName = null);

/// <summary>Every assignment of one principal, with the tenant-wide permissions gathered once.</summary>
public sealed class PrincipalGrants
{
    public PrincipalGrants(IReadOnlyList<Grant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        Grants = grants;
        TenantWide = grants.Where(g => g.Kind == RoleScopeKind.Tenant).SelectMany(g => g.Permissions).ToHashSet(StringComparer.Ordinal);
        Anywhere = grants.SelectMany(g => g.Permissions).ToHashSet(StringComparer.Ordinal);
        HasUnitGrants = grants.Any(g => g.Kind is RoleScopeKind.Unit or RoleScopeKind.UnitWithChildren);
    }

    public IReadOnlyList<Grant> Grants { get; }

    public IReadOnlySet<string> TenantWide { get; }

    /// <summary>Every permission held at any scope: what the shell's navigation shows (ADR-0031); the API still decides per row.</summary>
    public IReadOnlySet<string> Anywhere { get; }

    public bool HasUnitGrants { get; }

    /// <summary>The same grants with only these permissions left in them: a delegator's grants seen through the duties delegated.</summary>
    public PrincipalGrants LimitedTo(IReadOnlySet<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return new PrincipalGrants([.. Grants.Select(g => g with { Permissions = [.. g.Permissions.Where(permissions.Contains)] })]);
    }

    /// <summary>
    /// The pure rule (ADR-0030): tenant-wide grants apply everywhere; a unit grant applies to its unit; a grant with
    /// children applies to its unit and every unit below it; a record grant applies to its record. <paramref name="unitPath"/>
    /// is the ids from the root to the scope's unit, or empty when the scope has no unit or the unit is unknown.
    /// </summary>
    public HashSet<string> PermissionsAt(AccessScope scope, IReadOnlyList<Guid> unitPath)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(unitPath);
        if (scope.Kind == AccessScopeKind.Anywhere)
        {
            return new HashSet<string>(Anywhere, StringComparer.Ordinal);
        }

        var result = new HashSet<string>(TenantWide, StringComparer.Ordinal);
        if (scope.Kind == AccessScopeKind.Tenant)
        {
            return result;
        }

        foreach (var grant in Grants.Where(g => Applies(g, scope, unitPath)))
        {
            result.UnionWith(grant.Permissions);
        }

        return result;
    }

    /// <summary>
    /// The grants that give a permission at a scope, by the same rule as <see cref="PermissionsAt"/>: what an explanation
    /// names for an allowance (TF-039 AC2, ADR-0039).
    /// </summary>
    public IReadOnlyList<Grant> GrantsGiving(string permission, AccessScope scope, IReadOnlyList<Guid> unitPath)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(unitPath);
        return [.. Grants.Where(g => g.Permissions.Contains(permission, StringComparer.Ordinal)
            && (scope.Kind == AccessScopeKind.Anywhere || g.Kind == RoleScopeKind.Tenant || (scope.Kind != AccessScopeKind.Tenant && Applies(g, scope, unitPath))))];
    }

    /// <summary>A unit grant applies to its unit, a grant with children to its unit and every unit below it, a record grant to its record.</summary>
    private static bool Applies(Grant grant, AccessScope scope, IReadOnlyList<Guid> unitPath)
    {
        var unit = unitPath.Count > 0 ? unitPath[^1] : (Guid?)null;
        return grant.Kind switch
        {
            RoleScopeKind.Unit => unit is not null && grant.ScopeId == unit,
            RoleScopeKind.UnitWithChildren => grant.ScopeId is { } top && unitPath.Contains(top),
            RoleScopeKind.Record => scope.Kind == AccessScopeKind.Record && grant.ScopeId == scope.RecordId && string.Equals(grant.RecordType, scope.RecordType, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// What the principal holds over the whole of a target scope (ADR-0031): the tenant's tenant-wide permissions; a
    /// unit's permissions at that unit; for a unit with its children only the grants that reach every unit below it
    /// (tenant-wide, or with children at the unit or an ancestor); a record's permissions at the record.
    /// <paramref name="unitPath"/> is the ids from the root to the target unit, or empty.
    /// </summary>
    public HashSet<string> PermissionsCovering(RoleScope target, IReadOnlyList<Guid> unitPath)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(unitPath);
        switch (target.Kind)
        {
            case RoleScopeKind.Tenant:
                return new HashSet<string>(TenantWide, StringComparer.Ordinal);
            case RoleScopeKind.Unit:
                return unitPath.Count == 0 ? new HashSet<string>(TenantWide, StringComparer.Ordinal) : PermissionsAt(AccessScope.Unit(unitPath[^1]), unitPath);
            case RoleScopeKind.UnitWithChildren:
                var covering = new HashSet<string>(TenantWide, StringComparer.Ordinal);
                foreach (var grant in Grants.Where(g => g.Kind == RoleScopeKind.UnitWithChildren && g.ScopeId is { } top && unitPath.Contains(top)))
                {
                    covering.UnionWith(grant.Permissions);
                }

                return covering;
            default:
                return PermissionsAt(AccessScope.Record(target.RecordType!, target.RecordId!.Value, null), []);
        }
    }
}

/// <summary>
/// Loads a principal's assignments with their roles' permissions, caches them for at most 60 seconds (ADR-0012), and
/// answers for a scope or for a list (ADR-0030). A record or unit check reads one unit's path by its key; a list check
/// runs one query over the units, whatever the list's length. The request's principal acting for someone (ADR-0037)
/// gets the delegator's grants limited to the delegated duties, here, so every check, list and grant rule follows.
/// </summary>
public sealed class AccessGrants(AuthorizationDbContext context, IMemoryCache cache, PermissionCacheOptions options, TimeProvider time, ActingScope acting)
{
    /// <summary>Entries are kept in the memory cache for a while; the window itself is checked against Foundation's clock.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public static string CacheKey(Guid principalId) => $"authz:grants:{principalId:N}";

    public async Task<PrincipalGrants> OfAsync(Guid principalId, CancellationToken cancellationToken) =>
        (await WithActingAsync(principalId, cancellationToken).ConfigureAwait(false)).Grants;

    /// <summary>The grants a principal's checks use, and whose they are: the delegator's, limited, while acting (ADR-0037).</summary>
    private async Task<(PrincipalGrants Grants, ActingSubstitution? Acting)> WithActingAsync(Guid principalId, CancellationToken cancellationToken)
    {
        if (await acting.ForAsync(principalId, cancellationToken).ConfigureAwait(false) is { } substitution)
        {
            var delegators = await LoadAsync(substitution.DelegatorId, cancellationToken).ConfigureAwait(false);
            return (substitution.Limit is null ? delegators : delegators.LimitedTo(substitution.Limit), substitution);
        }

        return (await LoadAsync(principalId, cancellationToken).ConfigureAwait(false), null);
    }

    /// <summary>A principal's own grants, from the cache when it is fresh.</summary>
    private async Task<PrincipalGrants> LoadAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var key = CacheKey(principalId);
        var now = time.GetUtcNow();
        if (cache.TryGetValue(key, out CachedGrants? cached) && cached is not null && cached.ExpiresAt > now)
        {
            return cached.Grants;
        }

        var rows = await context.RoleAssignments.AsNoTracking()
            .Where(assignment => assignment.PrincipalId == principalId)
            .Join(context.Roles.AsNoTracking(), assignment => assignment.RoleId, role => role.Id, (assignment, role) => new { assignment.Id, assignment.ScopeKind, assignment.ScopeId, assignment.ScopeRecordType, role.Permissions, RoleId = role.Id, role.Code, role.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var grants = new PrincipalGrants(rows.Select(row => new Grant(Scopes.KindOf(row.ScopeKind), row.ScopeId, row.ScopeRecordType, row.Permissions, row.Id, row.RoleId, row.Code, row.Name)).ToList());
        var seconds = Math.Clamp(options.CacheSeconds, 0, PermissionCacheOptions.MaxCacheSeconds);
        if (seconds > 0)
        {
            cache.Set(key, new CachedGrants(grants, now.AddSeconds(seconds)), Retention);
        }

        return grants;
    }

    public void Forget(Guid principalId) => cache.Remove(CacheKey(principalId));

    /// <summary>The permissions a principal holds at a scope. Reads one unit's path when the principal has unit grants at all.</summary>
    public async Task<IReadOnlySet<string>> PermissionsAtAsync(Guid principalId, AccessScope scope, CancellationToken cancellationToken) =>
        (await AtAsync(principalId, scope, cancellationToken).ConfigureAwait(false)).Permissions;

    /// <summary>
    /// The principal's grants at a scope: what the evaluator's check and its explanation both read (ADR-0039), the
    /// permissions held there, the grants that give each, and the delegator whose grants counted while acting.
    /// </summary>
    public async Task<ScopedGrants> AtAsync(Guid principalId, AccessScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var (grants, substitution) = await WithActingAsync(principalId, cancellationToken).ConfigureAwait(false);
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

        return new ScopedGrants(grants, scope, unitPath, substitution);
    }

    /// <summary>What the principal holds over the whole of a target scope (ADR-0031); reads the target unit's path when there is one.</summary>
    public async Task<IReadOnlySet<string>> PermissionsCoveringAsync(Guid principalId, RoleScope target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var grants = await OfAsync(principalId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Guid> unitPath = [];
        if (target.UnitId is { } unitId)
        {
            var path = await context.OrganisationUnits.AsNoTracking()
                .Where(unit => unit.Id == unitId)
                .Select(unit => unit.Path)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            unitPath = path is null ? [] : OrganisationUnit.PathIds(path);
        }

        return grants.PermissionsCovering(target, unitPath);
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

/// <summary>A principal's grants read for one scope: the permissions held there, and the grants that give one of them.</summary>
public sealed record ScopedGrants(PrincipalGrants Grants, AccessScope Scope, IReadOnlyList<Guid> UnitPath, ActingSubstitution? Acting)
{
    public IReadOnlySet<string> Permissions => Grants.PermissionsAt(Scope, UnitPath);

    public IReadOnlyList<Grant> Giving(string permission) => Grants.GrantsGiving(permission, Scope, UnitPath);
}
