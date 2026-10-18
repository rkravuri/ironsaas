using Iron.Data.Entities;

namespace Iron.Audit.Data;

/// <summary>One row of <c>audit.events</c> (TF-060): written once, never changed. The app role cannot update or delete it.</summary>
public sealed class AuditEventRecord : AppendOnlyEntity
{
    public const int EventTypeMaxLength = 100;
    public const int TargetMaxLength = 500;
    public const int ReasonMaxLength = 1000;

    private AuditEventRecord()
    {
        EventType = string.Empty;
        Outcome = string.Empty;
        Source = string.Empty;
    }

    public AuditEventRecord(
        string eventType,
        string outcome,
        Guid actorId,
        string source,
        string? target,
        string? recordType,
        Guid? recordId,
        string? reason,
        string? details,
        string? correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        EventType = eventType;
        Outcome = outcome;
        ActorId = actorId;
        Source = source;
        Target = target;
        RecordType = recordType;
        RecordId = recordId;
        Reason = reason;
        Details = details;
        CorrelationId = correlationId;
    }

    public string EventType { get; private set; }

    public string Outcome { get; private set; }

    /// <summary>The principal the event is about. Equal to created_by in a request; the system principal writes on behalf of others in jobs.</summary>
    public Guid ActorId { get; private set; }

    /// <summary>Who was impersonating the actor, when anyone was (S2).</summary>
    public Guid? ImpersonatorId { get; private set; }

    public string Source { get; private set; }

    public string? Target { get; private set; }

    public string? RecordType { get; private set; }

    public Guid? RecordId { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>Small key-value details as JSON (jsonb).</summary>
    public string? Details { get; private set; }

    /// <summary>The trace id of the request or job that raised the event.</summary>
    public string? CorrelationId { get; private set; }
}
