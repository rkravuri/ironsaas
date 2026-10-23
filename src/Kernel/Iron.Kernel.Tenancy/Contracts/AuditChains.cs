namespace Iron.Kernel.Tenancy.Contracts;

/// <summary>One anchor a unit sent (ADR-0050): how far its chain reached, with which hash, and when the unit said so.</summary>
public sealed record AuditAnchorInfo(string Kind, long Position, string Hash, DateTimeOffset At, DateTimeOffset ReceivedAt);

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
    DateTimeOffset At);

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

    /// <summary>Every anchor of the tenant, in position order.</summary>
    Task<IReadOnlyList<AuditAnchorInfo>> AnchorsAsync(string slug, CancellationToken cancellationToken);

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
