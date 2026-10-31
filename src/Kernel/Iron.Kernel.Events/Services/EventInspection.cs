using System.Globalization;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Events.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Events.Services;

/// <summary>Where an event stands with one subscriber.</summary>
public sealed record DeliveryView(string Subscriber, string Status, int Attempts, DateTimeOffset NextAttemptAt, string? LastError, DateTimeOffset? HandledAt, DateTimeOffset? DeadAt, int Replays)
{
    public static DeliveryView Of(InboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new DeliveryView(entry.Subscriber, entry.Status, entry.Attempts, entry.NextAttemptAt, entry.LastError, entry.HandledAt, entry.DeadAt, entry.Replays);
    }
}

/// <summary>An event with where it stands with each of its subscribers.</summary>
public sealed record EventView(OutboxEvent Event, IReadOnlyList<DeliveryView> Deliveries)
{
    /// <summary>One word for the event: waiting while any subscriber has it pending (or it is not handed on yet), dead while any parked it, handled when all did, or no subscriber.</summary>
    public string Status => Summary(Event.FannedAt is null, Deliveries.Select(d => d.Status));

    public static string Summary(bool waiting, IEnumerable<string> statuses)
    {
        var all = statuses.ToList();
        if (waiting || all.Contains(DeliveryStates.Pending))
        {
            return DeliveryStates.Pending;
        }

        if (all.Contains(DeliveryStates.Dead))
        {
            return DeliveryStates.Dead;
        }

        return all.Count == 0 ? NoSubscriber : DeliveryStates.Handled;
    }

    public const string NoSubscriber = "no_subscriber";
}

/// <summary>A replay to one subscriber: of these events, or of every event it reads between two positions, with a reason.</summary>
public sealed record ReplayRequest(string Subscriber, IReadOnlyList<Guid>? EventIds, long? FromPosition, long? ToPosition, string Reason);

/// <summary>What a replay did: of the events it named, those put back to pending, those the subscriber had handled (left as they were), and those it had never received (given to it).</summary>
public sealed record ReplayResult(string Subscriber, int Events, int Requeued, int AlreadyHandled, int Added);

/// <summary>The archives' boundary: how long handled events stay, and the latest time an archive reaches.</summary>
public sealed record RetentionView(int Days, DateTimeOffset? ArchivedThrough, int Archives);

/// <summary>
/// What an administrator sees of the events and does with them (TF-112, ADR-0060): an event with its subscribers, the
/// archives' boundary, and a replay to one subscriber, with a reason, audited. A replay is idempotent through the inbox: an
/// event the subscriber handled stays handled, so its handler never runs twice; a dead letter goes back to pending and is
/// delivered again; an event it never received (it subscribed later) is given to it.
/// </summary>
public sealed class EventInspection(
    EventsDbContext context,
    EventDeclarations declarations,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal current,
    Iron.Kernel.Settings.Contracts.ISettings settings,
    TimeProvider time)
{
    /// <summary>The most events one replay names.</summary>
    public const int ReplayMaximum = 1000;

    public const int ReasonMaxLength = 500;

    public async Task<Result<EventView>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var outboxEvent = await context.Outbox.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false);
        if (outboxEvent is null)
        {
            // Event identifiers are ordered by time (UUID version 7): one from before the latest archive's end was archived, or never written.
            var through = await context.Archives.MaxAsync(a => (DateTimeOffset?)a.ToTime, cancellationToken).ConfigureAwait(false);
            if (through is { } end && TimeOf(id) is { } at && at <= end)
            {
                return Error.NotFound(EventsErrors.EventArchived, $"This event is not in the outbox any more: the events every subscriber handled were archived to the tenant's container up to {end.ToString("u", CultureInfo.InvariantCulture)}, after the retention. Ask the platform operator for the archive if you need it.");
            }

            return Error.NotFound(EventsErrors.EventNotFound, "There is no event with this id.");
        }

        var deliveries = await DeliveriesAsync([id], cancellationToken).ConfigureAwait(false);
        return new EventView(outboxEvent, deliveries.GetValueOrDefault(id) ?? []);
    }

    /// <summary>The deliveries of these events, by event.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DeliveryView>>> DeliveriesAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken)
    {
        var rows = await context.Inbox.AsNoTracking().Where(i => eventIds.Contains(i.EventId)).OrderBy(i => i.Subscriber).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.GroupBy(r => r.EventId).ToDictionary(g => g.Key, g => (IReadOnlyList<DeliveryView>)[.. g.Select(DeliveryView.Of)]);
    }

    public async Task<RetentionView> RetentionAsync(CancellationToken cancellationToken)
    {
        var days = await settings.GetAsync(EventsSettings.RetentionDays, Iron.Kernel.Settings.Contracts.SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var through = await context.Archives.MaxAsync(a => (DateTimeOffset?)a.ToTime, cancellationToken).ConfigureAwait(false);
        var count = await context.Archives.CountAsync(cancellationToken).ConfigureAwait(false);
        return new RetentionView(days, through, count);
    }

    /// <summary>Replays events to one subscriber (TF-112 AC1), as the person asking, or for the operator named in <paramref name="operatorName"/>.</summary>
    public async Task<Result<ReplayResult>> ReplayAsync(ReplayRequest request, string? operatorName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            return Error.Validation(EventsErrors.ReplayReasonRequired, "Say why you replay these events: the audit trail keeps it.", new FieldError("reason", "required", "Say why."));
        }

        var subscription = declarations.FindSubscription(request.Subscriber);
        if (subscription is null)
        {
            return Error.NotFound(EventsErrors.SubscriberUnknown, $"No module of this unit holds the subscription '{request.Subscriber}'.");
        }

        var type = subscription.Event.Name;
        var version = subscription.Event.Version;
        var query = context.Outbox.AsNoTracking().Where(e => e.Type == type && e.EventVersion == version);
        if (request.EventIds is { Count: > 0 } ids)
        {
            if (ids.Count > ReplayMaximum)
            {
                return Error.Validation(EventsErrors.ReplayTooMany, $"A replay names at most {ReplayMaximum} events.");
            }

            query = query.Where(e => ids.Contains(e.Id));
        }
        else
        {
            var from = request.FromPosition ?? 1;
            var to = request.ToPosition ?? long.MaxValue;
            query = query.Where(e => e.Position >= from && e.Position <= to);
        }

        var events = await query.OrderBy(e => e.Position).Take(ReplayMaximum + 1).Select(e => new { e.Id, e.Position, e.RecordType, e.RecordId, e.FannedAt }).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (events.Count > ReplayMaximum)
        {
            return Error.Validation(EventsErrors.ReplayTooMany, $"The range holds more than {ReplayMaximum} events of {subscription.Event}: replay a narrower one.");
        }

        if (events.Count == 0)
        {
            return Error.Validation(EventsErrors.ReplayNothing, $"No event of {subscription.Event}, which {subscription.Name} reads, is in the outbox for what you named.");
        }

        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var eventIds = events.Select(e => e.Id).ToList();
        var existing = await context.Inbox.Where(i => i.Subscriber == subscription.Name && eventIds.Contains(i.EventId)).ToDictionaryAsync(i => i.EventId, cancellationToken).ConfigureAwait(false);
        int requeued = 0, handled = 0, added = 0;
        foreach (var outboxEvent in events)
        {
            if (existing.TryGetValue(outboxEvent.Id, out var entry))
            {
                if (entry.Requeue(now))
                {
                    requeued++;
                }
                else
                {
                    handled++;
                }
            }
            else if (outboxEvent.FannedAt is not null)
            {
                // Handed on before this subscriber existed: it gets the event now. One not handed on yet gets it then.
                context.Inbox.Add(new InboxEntry(subscription.Name, outboxEvent.Id, outboxEvent.Position, outboxEvent.RecordType, outboxEvent.RecordId, now));
                added++;
            }
            else
            {
                handled++;
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["subscriber"] = subscription.Name,
            ["event_type"] = type,
            ["event_version"] = version.ToString(CultureInfo.InvariantCulture),
            ["events"] = events.Count.ToString(CultureInfo.InvariantCulture),
            ["requeued"] = requeued.ToString(CultureInfo.InvariantCulture),
            ["already_handled"] = handled.ToString(CultureInfo.InvariantCulture),
            ["added"] = added.ToString(CultureInfo.InvariantCulture),
            ["from_position"] = events[0].Position.ToString(CultureInfo.InvariantCulture),
            ["to_position"] = events[^1].Position.ToString(CultureInfo.InvariantCulture),
        };
        if (operatorName is not null)
        {
            details["operator"] = operatorName;
        }

        await audit.WriteAsync(new AuditEvent(EventsAuditEvents.ReplayRequested, AuditOutcome.Success, current.Id, subscription.Name, details, EventRecords.RecordType, events.Count == 1 ? events[0].Id : null, reason, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ReplayResult(subscription.Name, events.Count, requeued, handled, added);
    }

    /// <summary>The time a UUID version 7 carries in its first 48 bits, or null for another kind of id.</summary>
    public static DateTimeOffset? TimeOf(Guid id)
    {
        var bytes = id.ToByteArray(bigEndian: true);
        if (bytes[6] >> 4 != 7)
        {
            return null;
        }

        long milliseconds = 0;
        for (var i = 0; i < 6; i++)
        {
            milliseconds = (milliseconds << 8) | bytes[i];
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }
}
