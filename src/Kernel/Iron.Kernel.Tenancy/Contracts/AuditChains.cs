namespace Iron.Kernel.Tenancy.Contracts;

/// <summary>
/// One anchor a unit sent (ADR-0050): how far its chain reached, with which hash, when the unit said so, and the segment
/// of the chain it belongs to (1 until an operator accepts a restore).
/// </summary>
public sealed record AuditAnchorInfo(string Kind, long Position, string Hash, DateTimeOffset At, DateTimeOffset ReceivedAt, int Segment = 1)
{
    /// <summary>The anchor's identifier in the control plane, which an evidence pack's manifest names (ADR-0052); none for one not stored.</summary>
    public Guid? Id { get; init; }
}

/// <summary>
/// A restore an operator accepted (ADR-0050): the chain continues as segment <paramref name="Segment"/> from
/// <paramref name="StartPosition"/>, its first event linking to <paramref name="PreviousHash"/>, the restore point's hash;
/// the positions from <paramref name="LostFrom"/> to <paramref name="LostTo"/> were lost with the restore. Kept for good.
/// </summary>
public sealed record AuditRestoreInfo(
    Guid Id,
    string TenantSlug,
    int Segment,
    long StartPosition,
    string PreviousHash,
    long RestorePosition,
    DateTimeOffset RestoreAt,
    long LostFrom,
    long LostTo,
    string Ticket,
    string Reason,
    string AcceptedBy,
    DateTimeOffset AcceptedAt);

/// <summary>Something the verification found wrong, at a position, with the event's identifier when it has one.</summary>
public sealed record AuditChainProblemInfo(string Kind, long Position, Guid? EventId, string Message);

/// <summary>
/// One run of the fleet's verification of a tenant's chain (TF-061): the positions it checked, its result, what it found,
/// and after a restore, the last position that matches an anchor and that anchor's time (AC3).
/// </summary>
public sealed record AuditVerificationInfo(
    string TenantSlug,
    Guid? JobId,
    bool Full,
    long FromPosition,
    long ToPosition,
    string? ToHash,
    string Result,
    int EventsChecked,
    IReadOnlyList<AuditChainProblemInfo> Problems,
    long? RestorePosition,
    DateTimeOffset? RestoreAt,
    DateTimeOffset At)
{
    /// <summary>The record's identifier in the control plane, as an evidence pack lists it (ADR-0052); none before it is stored.</summary>
    public Guid? Id { get; init; }
}

public static class AuditVerificationResults
{
    /// <summary>Every position in the range is there, every hash recomputes, and every anchor matches.</summary>
    public const string Passed = "passed";

    /// <summary>An event was changed or deleted, or the chain disagrees with an anchor: an alert is raised.</summary>
    public const string Failed = "failed";

    /// <summary>The chain matches the anchors up to a point and ends before the latest: the database was restored.</summary>
    public const string Restored = "restored";
}

/// <summary>A verification the fleet owes: a tenant's daily or monthly run, or one an operator asked for (TF-061).</summary>
public sealed record AuditVerificationDue(string TenantSlug, string DatabaseName, string StorageContainer, bool Full, Guid? RequestId);

/// <summary>
/// The tenants' audit chains as the control plane keeps them (TF-061, ADR-0050): the anchors the units send, the fleet's
/// verification records, and which control-plane client is which tenant's unit. The fleet verifies against them.
/// </summary>
public interface ITenantAuditChains
{
    /// <summary>Makes this control-plane service account the tenant's unit client: its anchors are that tenant's.</summary>
    Task BindUnitClientAsync(string slug, Guid principalId, CancellationToken cancellationToken);

    /// <summary>Every anchor of the tenant, in position order, each with its segment.</summary>
    Task<IReadOnlyList<AuditAnchorInfo>> AnchorsAsync(string slug, CancellationToken cancellationToken);

    /// <summary>The restores operators accepted for the tenant, oldest first: the chain's segments after the first.</summary>
    Task<IReadOnlyList<AuditRestoreInfo>> RestoresAsync(string slug, CancellationToken cancellationToken);

    /// <summary>The tenant's latest verification, or its latest full one.</summary>
    Task<AuditVerificationInfo?> LatestVerificationAsync(string slug, bool fullOnly, CancellationToken cancellationToken);

    /// <summary>
    /// What is due now: every request an operator made, then each active tenant whose last verification is older than a
    /// day, in full when its last full one is older than a month.
    /// </summary>
    Task<IReadOnlyList<AuditVerificationDue>> DueAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Stores a verification and closes the request it answers; one that did not pass also raises the alert.</summary>
    Task RecordVerificationAsync(AuditVerificationInfo verification, Guid? requestId, CancellationToken cancellationToken);
}
