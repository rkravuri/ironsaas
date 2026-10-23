using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Compliance.Data;

/// <summary>
/// An electronic signature (TF-063, ADR-0051), written once and never changed: the signer, their full name and login
/// identifier at signing, the method, the server's time (<see cref="AppendOnlyEntity.CreatedAt"/>), the meaning, an
/// optional comment, the record's type, id and version, and the snapshot of the record at that version in its canonical
/// form with its SHA-256. The snapshot is kept so the hash can be checked, and so a changed record still shows what was
/// signed.
/// </summary>
public sealed class Signature : AppendOnlyEntity, ITenantWide
{
    public const string RecordTypeName = "signature";
    public const int RecordTypeMaxLength = 100;
    public const int CommentMaxLength = 500;

    private Signature()
    {
        RecordType = string.Empty;
        Snapshot = string.Empty;
        SnapshotHash = string.Empty;
        SignerName = string.Empty;
        SignerLoginId = string.Empty;
        Method = string.Empty;
        Meaning = string.Empty;
    }

    public Signature(string recordType, Guid recordId, int recordVersion, string snapshot, string snapshotHash, Guid signerId, string signerName, string signerLoginId, string method, string meaning, string? comment, Guid? sessionId)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotHash);
        RecordType = recordType;
        RecordId = recordId;
        RecordVersion = recordVersion;
        Snapshot = snapshot;
        SnapshotHash = snapshotHash;
        SnapshotForm = Contracts.SnapshotCanonicalForm.Version;
        SignerId = signerId;
        SignerName = signerName;
        SignerLoginId = signerLoginId;
        Method = method;
        Meaning = meaning;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, CommentMaxLength)];
        SessionId = sessionId;
    }

    public string RecordType { get; private set; }

    public Guid RecordId { get; private set; }

    public int RecordVersion { get; private set; }

    /// <summary>The snapshot as written by its canonical form: the exact text the hash was made from.</summary>
    public string Snapshot { get; private set; }

    public string SnapshotHash { get; private set; }

    public short SnapshotForm { get; private set; }

    public Guid SignerId { get; private set; }

    public string SignerName { get; private set; }

    public string SignerLoginId { get; private set; }

    public string Method { get; private set; }

    public string Meaning { get; private set; }

    public string? Comment { get; private set; }

    /// <summary>The session the signer signed in, so a review can follow a series of signatures.</summary>
    public Guid? SessionId { get; private set; }
}

/// <summary>The outcome of a review of the audit trail (TF-219 AC3).</summary>
public static class AuditReviewOutcomes
{
    public const string NoFindings = "no_findings";
    public const string Findings = "findings";

    public static IReadOnlyList<string> All => [NoFindings, Findings];
}

/// <summary>
/// A periodic review of the audit trail (TF-219 AC3), written once and signed with the meaning "reviewed": the period, the
/// scope (units and event types, none meaning all), the outcome with its notes, the searches the reviewer ran, and the
/// reviewer. Its signature is found by its record type and id.
/// </summary>
public sealed class AuditTrailReview : AppendOnlyEntity, ITenantWide
{
    public const string RecordType = "audit_trail_review";
    public const int NotesMaxLength = 4000;

    private AuditTrailReview()
    {
        UnitIds = [];
        EventTypes = [];
        Outcome = AuditReviewOutcomes.NoFindings;
        SearchesJson = "[]";
        ReviewerName = string.Empty;
    }

    public AuditTrailReview(DateOnly periodFrom, DateOnly periodTo, IReadOnlyList<Guid> unitIds, IReadOnlyList<string> eventTypes, string outcome, string? notes, string searchesJson, Guid reviewerId, string reviewerName)
        : this()
    {
        ArgumentNullException.ThrowIfNull(unitIds);
        ArgumentNullException.ThrowIfNull(eventTypes);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchesJson);
        PeriodFrom = periodFrom;
        PeriodTo = periodTo;
        UnitIds = [.. unitIds.Distinct().Order()];
        EventTypes = [.. eventTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Outcome = outcome;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        SearchesJson = searchesJson;
        ReviewerId = reviewerId;
        ReviewerName = reviewerName;
    }

    public DateOnly PeriodFrom { get; private set; }

    public DateOnly PeriodTo { get; private set; }

    public List<Guid> UnitIds { get; private set; }

    public List<string> EventTypes { get; private set; }

    public string Outcome { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>The searches the reviewer ran, as a JSON list of objects with a label and a link into the audit trail.</summary>
    public string SearchesJson { get; private set; }

    public Guid ReviewerId { get; private set; }

    public string ReviewerName { get; private set; }
}
