using Iron.Kernel.History;

namespace Iron.Kernel.Audit.Contracts;

/// <summary>
/// What happened, to what, by whom, with what outcome (TF-060). <paramref name="Action"/> is the event type, named by
/// the module that raises it like a permission: <c>authz.permission_denied</c>, <c>identity.login_failed</c>.
/// Details are small key-value pairs, never secrets. <paramref name="Severity"/> marks the few events someone must look
/// at, such as a sign-in with an emergency account (ADR-0049); the others are normal.
/// </summary>
public sealed record AuditEvent(
    string Action,
    string Outcome,
    Guid PrincipalId,
    string? Target = null,
    IReadOnlyDictionary<string, string>? Details = null,
    string? RecordType = null,
    Guid? RecordId = null,
    string? Reason = null,
    Guid? OnBehalfOfId = null,
    string? Severity = null,
    IReadOnlyList<Iron.Kernel.History.FieldChange>? Changes = null);

/// <summary>How much an event asks for attention (ADR-0049): normal, or high for one an administrator must look at.</summary>
public static class AuditSeverity
{
    public const string Normal = "normal";
    public const string High = "high";

    /// <summary>What is counted rather than looked at one by one: a request refused by a rate limit (ADR-0053).</summary>
    public const string Low = "low";
}

/// <summary>One action of a principal as the audit trail recorded it, for a review of what they did (TF-026 AC3).</summary>
public sealed record AuditTrailEntry(
    Guid Id,
    DateTimeOffset At,
    string EventType,
    string Outcome,
    string? Target,
    string? RecordType,
    Guid? RecordId,
    string? Reason);

/// <summary>
/// Reads the audit trail for a module that must account for a principal's actions, such as the review that ends a
/// break-glass access (TF-026 AC3, ADR-0049). The search endpoint remains how people read it.
/// </summary>
public interface IAuditTrail
{
    /// <summary>Every event with this principal as the actor, or as the one impersonated by them, from <paramref name="from"/>, oldest first.</summary>
    Task<IReadOnlyList<AuditTrailEntry>> ActionsOfAsync(Guid principalId, DateTimeOffset from, CancellationToken cancellationToken);
}

public static class AuditOutcome
{
    public const string Success = "success";
    public const string Denied = "denied";
    public const string Failure = "failure";
}

/// <summary>Where an event came from: a person in the shell, a client on the API, or a job (TF-060).</summary>
public static class AuditSource
{
    public const string Ui = "ui";
    public const string Api = "api";
    public const string Job = "job";
}

public static class AuditPermissions
{
    public static readonly Permission EventsRead = new("audit.events.read", "Search and read the audit trail, export it and see its chain", Risk.Medium);

    /// <summary>A legal hold stops records being deleted after their retention (TF-066 AC2): placed and released with a reason and step-up.</summary>
    public static readonly Permission LegalHoldsManage = new("audit.legal_holds.manage", "Place and release legal holds, with a reason", Risk.Critical);

    /// <summary>How long each kind of record is kept, within the operator's limits (TF-066 AC3).</summary>
    public static readonly Permission RetentionManage = new("audit.retention.manage", "Choose how long records and the audit trail are kept, within the operator's limits", Risk.High);

    public static IReadOnlyList<Permission> All => [EventsRead, LegalHoldsManage, RetentionManage];
}

/// <summary>The audit trail's own events (ADR-0050): its export, archive and pruning, retention and legal holds.</summary>
public static class AuditEvents
{
    public const string EventsExported = "audit.events_exported";

    /// <summary>An operator accepted a restore of the chain; it continues as a new segment (ADR-0050).</summary>
    public const string RestoreAccepted = "audit.restore_accepted";
    public const string TrailArchived = "audit.trail_archived";
    public const string TrailPruned = "audit.trail_pruned";
    public const string RecordExpired = "audit.record_expired";
    public const string LegalHoldPlaced = "audit.legal_hold_placed";
    public const string LegalHoldReleased = "audit.legal_hold_released";
    public const string RetentionChanged = "audit.retention_changed";

    /// <summary>A tracked record was created, with its fields (TF-062, ADR-0051).</summary>
    public const string RecordCreated = "record.created";

    /// <summary>A save changed a tracked record: every changed field with its old and new value (TF-062 AC1).</summary>
    public const string RecordChanged = "record.changed";

    /// <summary>A tracked record was deleted, with the values it had.</summary>
    public const string RecordDeleted = "record.deleted";

    /// <summary>Every event type above with the words it reads as in the audit trail, the History tab and the exports (ADR-0051).</summary>
    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        new AuditEventType(EventsExported, "Audit trail exported").AsAdministrative(),
        new AuditEventType(RestoreAccepted, "Audit chain restore accepted").AsAdministrative(),
        new(TrailArchived, "Audit trail archived"),
        new(TrailPruned, "Audit trail pruned"),
        new(RecordExpired, "Record removed at the end of its retention"),
        new AuditEventType(LegalHoldPlaced, "Legal hold placed").AsAdministrative(),
        new AuditEventType(LegalHoldReleased, "Legal hold released").AsAdministrative(),
        new AuditEventType(RetentionChanged, "Retention period changed").AsAdministrative(),
        new(RecordCreated, "Record created"),
        new(RecordChanged, "Record changed"),
        new(RecordDeleted, "Record deleted"),
    ];
}

public static class AuditErrors
{
    public const string RetentionClassUnknown = "audit.retention.class_unknown";

    /// <summary>A choice below the operator's minimum for the class (TF-066 AC3).</summary>
    public const string RetentionBelowMinimum = "audit.retention.below_minimum";

    public const string RetentionAboveMaximum = "audit.retention.above_maximum";
    public const string LegalHoldScopeInvalid = "audit.legal_hold.scope_invalid";
    public const string LegalHoldReasonRequired = "audit.legal_hold.reason_required";
    public const string LegalHoldNotFound = "audit.legal_hold.not_found";
    public const string LegalHoldReleased = "audit.legal_hold.released";

    /// <summary>A search of the archive needs a date range: the archive index finds batches by date (TF-064 AC2).</summary>
    public const string ArchiveRangeRequired = "audit.archive.range_required";

    public const string ExportNotFound = "audit.export.not_found";

    /// <summary>The search's address filter was empty or too long (ADR-0056).</summary>
    public const string AddressInvalid = "audit.events.address_invalid";
}

/// <summary>
/// Records an audit event as a row in <c>kernel_audit.events</c> on the scope's connection: inside the caller's unit of work
/// when one is open, so the event is committed with the change (TF-060 AC1), otherwise at once. Callers never swallow
/// a failure silently: they log it.
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}
