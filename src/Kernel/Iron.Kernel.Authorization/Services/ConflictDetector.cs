using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;

namespace Iron.Kernel.Authorization.Services;

/// <summary>A side of a rule: a permission's name, or a role's id.</summary>
public sealed record ConflictSide(string Kind, string Value);

/// <summary>A rule as the detector reads it.</summary>
public sealed record ConflictRuleView(Guid Id, string Name, string Mode, string Reason, ConflictSide First, ConflictSide Second);

/// <summary>
/// One right a person holds, as segregation of duties sees it: a role at a scope from an assignment, or the part of a
/// delegator's assignment a delegation hands over (its permissions limited to the duties). <see cref="UnitPath"/> is the
/// ids from the root to the scope's unit, empty for the tenant or a record. <see cref="IsNew"/> marks the grant or
/// delegation under consideration: only conflicts it takes part in are its doing.
/// </summary>
public sealed record HeldGrant(
    Guid? AssignmentId,
    Guid? DelegationId,
    Guid RoleId,
    string RoleName,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyCollection<string> RolePermissions,
    RoleScopeKind Kind,
    Guid? ScopeId,
    string? RecordType,
    IReadOnlyList<Guid> UnitPath,
    bool IsNew)
{
    /// <summary>The unit the grant names, for a unit grant.</summary>
    public Guid? UnitId => Kind is RoleScopeKind.Unit or RoleScopeKind.UnitWithChildren ? ScopeId : null;

    /// <summary>One key per grant: the assignment, or the delegation with the delegator's assignment it carries.</summary>
    public string Key => AssignmentId is { } assignment
        ? DelegationId is { } delegation ? $"{delegation:N}/{assignment:N}" : assignment.ToString("N")
        : $"new/{RoleId:N}/{Kind}/{ScopeId:N}";
}

/// <summary>A conflict: the rule, the grant that holds its first side, the one that holds its second, and the unit both reach.</summary>
public sealed record Conflict(ConflictRuleView Rule, HeldGrant First, HeldGrant Second, Guid? UnitId);

/// <summary>
/// The rule of segregation of duties (TF-036, ADR-0039), pure: a person holds both sides of a rule when one grant holds
/// the first side, one the second (they may be the same grant), and some unit lies within the scope of both. A tenant
/// grant reaches every unit; a unit grant its unit; a grant with children its unit and every unit below; a record grant
/// its record only, so it meets a tenant grant or a grant on the same record. Rights at two unrelated units are not a
/// conflict.
/// </summary>
public static class ConflictDetector
{
    /// <summary>
    /// Whether a grant holds a side: a permission side when the grant gives the permission; a role side when it is that
    /// role with every one of its permissions (a delegation of part of a role does not hold the role).
    /// </summary>
    public static bool Holds(ConflictSide side, HeldGrant grant)
    {
        ArgumentNullException.ThrowIfNull(side);
        ArgumentNullException.ThrowIfNull(grant);
        return side.Kind == ConflictSides.Permission
            ? grant.Permissions.Contains(side.Value, StringComparer.Ordinal)
            : string.Equals(grant.RoleId.ToString("D"), side.Value, StringComparison.OrdinalIgnoreCase) && grant.RolePermissions.All(p => grant.Permissions.Contains(p, StringComparer.Ordinal));
    }

    /// <summary>Whether some unit lies within both scopes, and the deepest unit they share (null for the tenant or a record).</summary>
    public static (bool Overlaps, Guid? Unit) Overlap(HeldGrant first, HeldGrant second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.Kind == RoleScopeKind.Tenant)
        {
            return (true, second.UnitId);
        }

        if (second.Kind == RoleScopeKind.Tenant)
        {
            return (true, first.UnitId);
        }

        if (first.Kind == RoleScopeKind.Record || second.Kind == RoleScopeKind.Record)
        {
            var same = first.Kind == second.Kind && first.ScopeId == second.ScopeId && string.Equals(first.RecordType, second.RecordType, StringComparison.Ordinal);
            return (same, null);
        }

        // Two unit grants: the deeper unit must lie within the other's reach.
        if (Reaches(first, second))
        {
            return (true, second.UnitId);
        }

        return Reaches(second, first) ? (true, first.UnitId) : (false, null);
    }

    /// <summary>
    /// The conflicts among a person's grants. With <paramref name="onlyNew"/>, those a new grant or delegation takes part
    /// in, which are its doing; otherwise all of them, for the report. One conflict per rule and pair of grants.
    /// </summary>
    public static IReadOnlyList<Conflict> Find(IEnumerable<ConflictRuleView> rules, IReadOnlyList<HeldGrant> grants, bool onlyNew)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(grants);
        var found = new List<Conflict>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            foreach (var first in grants.Where(g => Holds(rule.First, g)))
            {
                foreach (var second in grants.Where(g => Holds(rule.Second, g)))
                {
                    if (onlyNew && !first.IsNew && !second.IsNew)
                    {
                        continue;
                    }

                    var (overlaps, unit) = Overlap(first, second);
                    var pair = string.CompareOrdinal(first.Key, second.Key) <= 0 ? $"{first.Key}|{second.Key}" : $"{second.Key}|{first.Key}";
                    if (overlaps && seen.Add($"{rule.Id:N}|{pair}"))
                    {
                        found.Add(new Conflict(rule, first, second, unit));
                    }
                }
            }
        }

        return found;
    }

    /// <summary>Whether the outer grant reaches the inner grant's unit: the same unit, or the outer with its children above it.</summary>
    private static bool Reaches(HeldGrant outer, HeldGrant inner) =>
        outer.UnitId is { } top && inner.UnitId is { } unit
        && (top == unit || (outer.Kind == RoleScopeKind.UnitWithChildren && inner.UnitPath.Contains(top)));
}
