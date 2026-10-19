namespace Iron.Kernel;

/// <summary>
/// What one permission reaches in a list (TF-033, ADR-0030): every record of the tenant, or the records owned by a set of
/// units plus a set of records granted one by one. Computed once per list, then applied as one predicate in the list's
/// own query, so the cost does not grow with the number of rows.
/// </summary>
public sealed class RecordAccess
{
    private RecordAccess(bool everything, IReadOnlyList<Guid> units, IReadOnlyList<Guid> records)
    {
        IsEverything = everything;
        Units = units;
        Records = records;
    }

    /// <summary>A tenant-wide grant: no filter.</summary>
    public static RecordAccess Everything { get; } = new(true, [], []);

    public static RecordAccess Nothing { get; } = new(false, [], []);

    public bool IsEverything { get; }

    /// <summary>The units whose records are reached; empty for <see cref="Everything"/>.</summary>
    public IReadOnlyList<Guid> Units { get; }

    /// <summary>The records reached one by one through record-scoped assignments.</summary>
    public IReadOnlyList<Guid> Records { get; }

    public bool IsNothing => !IsEverything && Units.Count == 0 && Records.Count == 0;

    public static RecordAccess Within(IEnumerable<Guid> units, IEnumerable<Guid> records)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(records);
        return new RecordAccess(false, units.Distinct().Order().ToList(), records.Distinct().Order().ToList());
    }

    /// <summary>The same rule as the list predicate, for one record in memory.</summary>
    public bool Allows(Guid owningUnitId, Guid recordId) => IsEverything || Units.Contains(owningUnitId) || Records.Contains(recordId);
}
