namespace Iron.Kernel.History;

/// <summary>How a field's values are kept in its history (TF-062 AC3, ADR-0051).</summary>
public static class FieldTreatments
{
    /// <summary>The old and new values as they are.</summary>
    public const string Plain = "plain";

    /// <summary>The values masked, their last characters kept: a bank account as its last four.</summary>
    public const string Masked = "masked";

    /// <summary>No value at all, only that it changed: a password's hash, a token.</summary>
    public const string Secret = "secret";
}

/// <summary>One field of one save (TF-062 AC1): its name in snake_case, its old and new values as text, and how they are kept.</summary>
public sealed record FieldChange(string Field, string? Old, string? New, string Treatment = FieldTreatments.Plain);

public static class RecordChangeKinds
{
    public const string Created = "created";
    public const string Changed = "changed";
    public const string Deleted = "deleted";
}

/// <summary>
/// What one save did to one tracked record (TF-062, ADR-0051): created, changed or deleted, the record's type, id and
/// version after the save, and every tracked field that changed. It becomes one event of the audit trail.
/// </summary>
public sealed record RecordChange(string Kind, string RecordType, Guid RecordId, int? Version, IReadOnlyList<FieldChange> Fields);
