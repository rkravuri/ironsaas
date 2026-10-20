using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Audit.Data;

/// <summary>One row of <c>kernel_audit.events</c> (TF-060): written once, never changed. The app role cannot update or delete it.</summary>
public sealed class AuditEventRecord : AppendOnlyEntity, ITenantWide
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
        string? correlationId,
        Guid? onBehalfOfId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        EventType = eventType;
        Outcome = outcome;
        ActorId = actorId;
        Source = source;
        Target = Trim(target, TargetMaxLength);
        RecordType = recordType;
        RecordId = recordId;
        Reason = Trim(reason, ReasonMaxLength);
        Details = details;
        CorrelationId = correlationId;
        OnBehalfOfId = onBehalfOfId;
    }

    public string EventType { get; private set; }

    public string Outcome { get; private set; }

    /// <summary>The principal the event is about. Equal to created_by in a request; the system principal writes on behalf of others in jobs.</summary>
    public Guid ActorId { get; private set; }

    /// <summary>Who was impersonating the actor, when anyone was. Reserved for support impersonation; acting for someone is <see cref="OnBehalfOfId"/>.</summary>
    public Guid? ImpersonatorId { get; private set; }

    /// <summary>
    /// The person the actor acted for with a delegation (TF-037 AC3, ADR-0037): the actor did it, with that person's
    /// rights for the delegated duties. Null when the actor acted as themselves.
    /// </summary>
    public Guid? OnBehalfOfId { get; private set; }

    public string Source { get; private set; }

    public string? Target { get; private set; }

    public string? RecordType { get; private set; }

    public Guid? RecordId { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>Small key-value details as JSON (jsonb).</summary>
    public string? Details { get; private set; }

    /// <summary>The trace id of the request or job that raised the event.</summary>
    public string? CorrelationId { get; private set; }

    private static string? Trim(string? value, int max) => value is null || value.Length <= max ? value : value[..(max - 1)] + "\u2026";
}
