using Iron.Kernel.History;

namespace Iron.Kernel.Events.Contracts;

/// <summary>
/// Writes a domain event into the outbox, in the unit of work of the change that raises it (TF-085, ADR-0060): the event
/// commits with the change and its audit event, or not at all. Publishing outside a unit of work is a programmer's error
/// and throws, as does data that does not fit the event's declaration (TF-110 AC2). The dispatcher in the unit's worker
/// hands the event to its subscribers after the commit.
/// </summary>
public interface IEventPublisher
{
    /// <summary>Publishes <paramref name="data"/> as <paramref name="type"/> about the record <paramref name="recordId"/>; answers the event's id.</summary>
    Task<Guid> PublishAsync<TData>(EventType<TData> type, Guid recordId, TData data, CancellationToken cancellationToken)
        where TData : class;
}

/// <summary>The Events module's permissions (ADR-0060).</summary>
public static class EventsPermissions
{
    public static readonly Permission EventsRead = new("events.events.read", "Search the domain events, see which subscribers handled each one, and the dead letters.", Risk.Medium);

    /// <summary>A replay runs a subscriber's handler again on events; it never runs one twice on an event it handled (ADR-0060).</summary>
    public static readonly Permission EventsReplay = new("events.events.replay", "Replay events to a subscriber that failed them, or never received them.", Risk.High);

    public static IReadOnlyList<Permission> All => [EventsRead, EventsReplay];
}

/// <summary>The Events module's audit events (ADR-0060).</summary>
public static class EventsAuditEvents
{
    /// <summary>A subscriber failed an event as many times as it is retried: the event is parked for it, and an alert raised (TF-085 AC2).</summary>
    public const string DeliveryDeadLettered = "events.delivery_dead_lettered";

    /// <summary>Someone replayed events to a subscriber, with a reason (TF-112).</summary>
    public const string ReplayRequested = "events.replay_requested";

    /// <summary>Events past the retention were archived to the tenant's container and left the outbox (TF-112 AC2).</summary>
    public const string EventsArchived = "events.events_archived";

    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        new(DeliveryDeadLettered, "Event parked as a dead letter"),
        new AuditEventType(ReplayRequested, "Events replayed to a subscriber").AsAdministrative(),
        new(EventsArchived, "Events archived"),
    ];
}

public static class EventsErrors
{
    public const string EventNotFound = "events.event.not_found";

    /// <summary>The event is older than the retention: it left the outbox for the tenant's container (TF-112 AC2).</summary>
    public const string EventArchived = "events.event.archived";

    public const string SubscriberUnknown = "events.subscriber.unknown";
    public const string ReplayNothing = "events.replay.nothing";
    public const string ReplayTooMany = "events.replay.too_many";
    public const string ReplayReasonRequired = "events.replay.reason_required";
}

/// <summary>The Events module's setting (ADR-0036).</summary>
public static class EventsSettings
{
    /// <summary>How long an event every subscriber handled stays in the outbox before it is archived (TF-112 AC2).</summary>
    public static readonly Setting<int> RetentionDays = Setting.WholeNumber(
        "events.retention.days", "How many days a handled domain event stays searchable before it is archived to the tenant's container.", 90, 7, 3650, SettingScopes.Tenant);

    public static IReadOnlyList<SettingDefinition> All => [RetentionDays];
}

/// <summary>Where an event stands with one subscriber: waiting (or waiting to be retried), handled, or parked as a dead letter.</summary>
public static class DeliveryStates
{
    public const string Pending = "pending";
    public const string Handled = "handled";
    public const string Dead = "dead";

    /// <summary>
    /// For a subscriber of a suite the unit no longer runs (ADR-0060): kept, neither tried nor alerted, and pending again by
    /// itself when the suite is added back; removed with the suite's data when that is deleted.
    /// </summary>
    public const string Held = "held";
}

/// <summary>An event a subscriber parked, as the console's one view of every dead letter lists it beside the jobs' (ADR-0061).</summary>
public sealed record EventDeadLetter(Guid EventId, string Subscriber, string Type, int Version, string RecordType, Guid RecordId, int Attempts, string? LastError, DateTimeOffset DeadAt);

/// <summary>
/// The dead letters of the event subscriptions, newest first (ADR-0060, ADR-0061): what the job console's view of every
/// dead letter reads, through this contract, beside the jobs' own. A replay goes through the Events module's endpoint.
/// </summary>
public interface IEventDeadLetters
{
    Task<int> CountAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<EventDeadLetter>> NewestAsync(int skip, int take, CancellationToken cancellationToken);
}
