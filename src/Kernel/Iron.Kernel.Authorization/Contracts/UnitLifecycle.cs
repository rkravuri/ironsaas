namespace Iron.Kernel.Authorization.Contracts;

/// <summary>One thing a module needs of a new unit before people work in it, in words, and whether it is done (TF-223).</summary>
public sealed record ReadinessItem(string Module, string Code, string Label, bool Done);

/// <summary>
/// A module's checklist for a unit (TF-223, ADR-0038): a product that needs a numbering series, a ledger or a default
/// warehouse in each unit says so here, and the units page shows the list and whether the unit is ready. The kernel
/// contributes none. Register with <c>services.TryAddEnumerable(ServiceDescriptor.Scoped&lt;IUnitReadiness, T&gt;())</c>.
/// </summary>
public interface IUnitReadiness
{
    Task<IReadOnlyList<ReadinessItem>> CheckAsync(OrganisationUnitInfo unit, CancellationToken cancellationToken);
}

/// <summary>The parts a merge preview and the merge's audit event list, one code per kind of thing moved.</summary>
public static class UnitMergeParts
{
    public const string Records = "records";
    public const string People = "people";
    public const string Assignments = "assignments";
    public const string Settings = "settings";
    public const string Shares = "shares";
    public const string Children = "children";
}

/// <summary>
/// One kind of thing a merge moves from a unit into another (TF-223 AC1, ADR-0038): the module, for records the record
/// type's code and its display name, what to show of each one in words, their ids (keys for settings) for the audit
/// event, and what is dropped rather than moved because the surviving unit has its own.
/// </summary>
public sealed record UnitMergePart(string Code, string Module, string? RecordType, string? RecordTypeName, IReadOnlyList<string> Items, IReadOnlyList<string> Ids, IReadOnlyList<string> Conflicts)
{
    public int Count => Ids.Count;
}

/// <summary>
/// A module that keeps something at a unit other than business records, which move through <c>IUnitRecordOwners</c>:
/// Identity moves the people homed at the unit, Settings moves the unit's values. Authorization moves assignments and
/// child units itself. Register with <c>services.TryAddEnumerable(ServiceDescriptor.Scoped&lt;IUnitMergeParticipant, T&gt;())</c>.
/// </summary>
public interface IUnitMergeParticipant
{
    /// <summary>What would move, read without changing anything.</summary>
    Task<IReadOnlyList<UnitMergePart>> PreviewAsync(Guid fromUnitId, Guid intoUnitId, CancellationToken cancellationToken);

    /// <summary>Moves it, inside the merge's transaction; answers what moved.</summary>
    Task<IReadOnlyList<UnitMergePart>> ApplyAsync(Guid fromUnitId, Guid intoUnitId, CancellationToken cancellationToken);

    /// <summary>After the merge is committed: a module that caches what moved refreshes it here.</summary>
    Task MergedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
