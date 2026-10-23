using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Audit.Data;

/// <summary>
/// One archived batch of the audit trail (TF-064 AC2, ADR-0050): consecutive positions moved to the tenant's container
/// after the online period, with their chain columns. The index keeps them searchable by date range, and the verifier
/// reads the batch from the container; the batch's SHA-256 shows a changed file.
/// </summary>
public sealed class AuditArchiveBatch : AppendOnlyEntity, ITenantWide, IVerifierReadable
{
    public const int KeyMaxLength = 200;

    private AuditArchiveBatch()
    {
        BlobKey = string.Empty;
        ContentSha256 = string.Empty;
        LastHash = string.Empty;
    }

    public AuditArchiveBatch(long firstPosition, long lastPosition, DateTimeOffset firstAt, DateTimeOffset lastAt, int eventCount, string blobKey, string contentSha256, string lastHash)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstPosition, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastPosition, firstPosition);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobKey);
        FirstPosition = firstPosition;
        LastPosition = lastPosition;
        FirstAt = firstAt;
        LastAt = lastAt;
        EventCount = eventCount;
        BlobKey = blobKey;
        ContentSha256 = contentSha256;
        LastHash = lastHash;
    }

    public long FirstPosition { get; private set; }

    public long LastPosition { get; private set; }

    /// <summary>The oldest event's time in the batch: with <see cref="LastAt"/>, the date range the archive index searches.</summary>
    public DateTimeOffset FirstAt { get; private set; }

    public DateTimeOffset LastAt { get; private set; }

    public int EventCount { get; private set; }

    public string BlobKey { get; private set; }

    public string ContentSha256 { get; private set; }

    /// <summary>The hash of the batch's last event: the next batch, or the database, follows it.</summary>
    public string LastHash { get; private set; }
}

/// <summary>
/// The oldest end of the trail removed after its retention (TF-064, ADR-0050): everything up to a position, whose hash the
/// remaining chain starts from. The unit anchors it in the control plane, so the chain still verifies.
/// </summary>
public sealed class AuditPrune : AppendOnlyEntity, ITenantWide, IVerifierReadable
{
    private AuditPrune()
    {
        HashAtPosition = string.Empty;
    }

    public AuditPrune(long upToPosition, string hashAtPosition, int batchesRemoved)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(upToPosition, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(hashAtPosition);
        UpToPosition = upToPosition;
        HashAtPosition = hashAtPosition;
        BatchesRemoved = batchesRemoved;
    }

    public long UpToPosition { get; private set; }

    public string HashAtPosition { get; private set; }

    public int BatchesRemoved { get; private set; }
}

/// <summary>What a legal hold keeps: the whole tenant, one record, or every record linked to a master such as a supplier.</summary>
public static class LegalHoldScopes
{
    public const string Tenant = "tenant";
    public const string Record = "record";
    public const string Master = "master";
}

/// <summary>
/// A legal hold (TF-066 AC2, ADR-0050): while it stands, the retention job deletes nothing it names, and a hold on the
/// whole tenant keeps the audit trail's oldest end too. Placing and releasing it need a reason and step-up, and are audited.
/// </summary>
[TrackedAs("legal_hold")]
public sealed class LegalHold : AuditedEntity, ITenantWide, ITrackedChanges
{
    public const int ReasonMaxLength = 1000;
    public const int RecordTypeMaxLength = 100;

    private LegalHold()
    {
        Scope = string.Empty;
        Reason = string.Empty;
    }

    public LegalHold(string scope, string? recordType, Guid? recordId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Scope = scope;
        RecordType = recordType;
        RecordId = recordId;
        Reason = reason;
    }

    public string Scope { get; private set; }

    /// <summary>The record's type, or the master's for a master hold; null for the whole tenant.</summary>
    public string? RecordType { get; private set; }

    public Guid? RecordId { get; private set; }

    public string Reason { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    public Guid? ReleasedById { get; private set; }

    public string? ReleaseReason { get; private set; }

    public bool Active => ReleasedAt is null;

    public void Release(Guid by, string reason, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ReleasedAt = at;
        ReleasedById = by;
        ReleaseReason = reason;
    }
}

/// <summary>A tenant's choice of retention for one class (TF-066 AC3): within the operator's limits, checked when it is saved.</summary>
public sealed class RetentionChoice : AuditedEntity, ITenantWide, ITrackedChanges
{
    /// <summary>The record type of a choice's history (TF-062).</summary>
    public const string RecordType = "retention_choice";

    private RetentionChoice()
    {
        ClassCode = string.Empty;
    }

    public RetentionChoice(string classCode, int days)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(classCode);
        ClassCode = classCode;
        Days = days;
    }

    public string ClassCode { get; private set; }

    public int Days { get; private set; }

    public void Change(int days) => Days = days;
}

/// <summary>An export of an audit search to CSV (TF-064), kept as a file in the tenant's container and handed over as a download.</summary>
public sealed class AuditExport : AppendOnlyEntity, ITenantWide
{
    private AuditExport()
    {
        Filters = "{}";
        BlobKey = string.Empty;
        Sha256 = string.Empty;
    }

    public AuditExport(string filters, string blobKey, int rows, string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobKey);
        Filters = filters;
        BlobKey = blobKey;
        Rows = rows;
        Sha256 = sha256;
    }

    /// <summary>The search's filters as JSON, also written in the file's first lines.</summary>
    public string Filters { get; private set; }

    public string BlobKey { get; private set; }

    public int Rows { get; private set; }

    public string Sha256 { get; private set; }
}

/// <summary>
/// A restore an operator accepted, as the unit recorded it when the control plane told it (ADR-0050): the segment the
/// chain continues as, the restore point it continues from, the range the restore lost, and who accepted it, when, with
/// which ticket and why. Written once; the chain page shows every one for good.
/// </summary>
public sealed class AuditChainRestore : AppendOnlyEntity, ITenantWide
{
    /// <summary>The record type of the restore's own event in the trail.</summary>
    public const string RecordType = "audit_restore";

    private AuditChainRestore()
    {
        PreviousHash = string.Empty;
        Ticket = string.Empty;
        Reason = string.Empty;
        AcceptedBy = string.Empty;
    }

    public AuditChainRestore(int segment, long startPosition, string previousHash, long restorePosition, DateTimeOffset restoreAt, long lostFrom, long lostTo, string ticket, string reason, string acceptedBy, DateTimeOffset acceptedAt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(segment, 2);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousHash);
        Segment = segment;
        StartPosition = startPosition;
        PreviousHash = previousHash;
        RestorePosition = restorePosition;
        RestoreAt = restoreAt;
        LostFrom = lostFrom;
        LostTo = lostTo;
        Ticket = ticket ?? string.Empty;
        Reason = reason ?? string.Empty;
        AcceptedBy = acceptedBy ?? string.Empty;
        AcceptedAt = acceptedAt;
    }

    public int Segment { get; private set; }

    public long StartPosition { get; private set; }

    public string PreviousHash { get; private set; }

    public long RestorePosition { get; private set; }

    public DateTimeOffset RestoreAt { get; private set; }

    public long LostFrom { get; private set; }

    public long LostTo { get; private set; }

    public string Ticket { get; private set; }

    public string Reason { get; private set; }

    /// <summary>The operator's name, as the control plane gave it.</summary>
    public string AcceptedBy { get; private set; }

    /// <summary>When the operator accepted it in the control plane; the row's own time is when the unit heard of it.</summary>
    public DateTimeOffset AcceptedAt { get; private set; }
}
