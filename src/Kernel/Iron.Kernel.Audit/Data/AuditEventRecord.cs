using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Audit.Data;

/// <summary>One row of <c>kernel_audit.events</c> (TF-060): written once, never changed. The app role cannot update or delete it.</summary>
public sealed class AuditEventRecord : AppendOnlyEntity, ITenantWide, IVerifierReadable
{
    public const int EventTypeMaxLength = 100;
    public const int TargetMaxLength = 500;
    public const int ReasonMaxLength = 1000;
    public const int SeverityMaxLength = 10;

    private AuditEventRecord()
    {
        EventType = string.Empty;
        Outcome = string.Empty;
        Source = string.Empty;
        Severity = "normal";
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
        Guid? onBehalfOfId = null,
        Guid? impersonatorId = null,
        string? severity = null)
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
        ImpersonatorId = impersonatorId;
        Severity = string.IsNullOrWhiteSpace(severity) ? "normal" : severity;
    }

    public string EventType { get; private set; }

    public string Outcome { get; private set; }

    /// <summary>The principal the event is about. Equal to created_by in a request; the system principal writes on behalf of others in jobs.</summary>
    public Guid ActorId { get; private set; }

    /// <summary>
    /// Who was signed in as the actor, when anyone was (TF-025 AC2, ADR-0049): a colleague by their id, or the operator by
    /// the control plane's id of them, whose name the details keep. Acting for someone is <see cref="OnBehalfOfId"/>.
    /// </summary>
    public Guid? ImpersonatorId { get; private set; }

    /// <summary><c>normal</c>, or <c>high</c> for an event an administrator must look at (ADR-0049).</summary>
    public string Severity { get; private set; }

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

    /// <summary>The event's place in the tenant's chain, from 1, set once by the sealer (TF-061, ADR-0050); null until sealed.</summary>
    public long? ChainPosition { get; private set; }

    /// <summary>The hash of the event at the position before, or 64 zeros at position 1.</summary>
    public string? PreviousHash { get; private set; }

    /// <summary>SHA-256 of the canonical form (<see cref="Contracts.AuditCanonicalForm"/>) with the previous hash, in lowercase hexadecimal.</summary>
    public string? Hash { get; private set; }

    /// <summary>The canonical form's version the hash was made with.</summary>
    public short? HashVersion { get; private set; }

    /// <summary>When the sealer gave the event its place: the server's time, after the event was committed.</summary>
    public DateTimeOffset? SealedAt { get; private set; }

    /// <summary>The content the chain hashes, at the position the sealer gives it.</summary>
    public Contracts.ChainedEvent Chained(long position) =>
        new(Id, position, CreatedAt, CreatedBy, EventType, Outcome, ActorId, OnBehalfOfId, ImpersonatorId, Source, Severity, Target, RecordType, RecordId, Reason, CorrelationId, Details);

    private static string? Trim(string? value, int max) => value is null || value.Length <= max ? value : value[..(max - 1)] + "\u2026";
}
