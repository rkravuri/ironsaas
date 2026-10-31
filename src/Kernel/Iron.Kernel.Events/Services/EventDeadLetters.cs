using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Events.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Events.Services;

/// <summary>The subscriptions' dead letters, newest first, for the job console's view of every dead letter (ADR-0061).</summary>
public sealed class EventDeadLetters(EventsDbContext context) : IEventDeadLetters
{
    public Task<int> CountAsync(CancellationToken cancellationToken) =>
        context.Inbox.CountAsync(i => i.Status == DeliveryStates.Dead, cancellationToken);

    public async Task<IReadOnlyList<EventDeadLetter>> NewestAsync(int skip, int take, CancellationToken cancellationToken)
    {
        var rows = await context.Inbox.AsNoTracking()
            .Where(i => i.Status == DeliveryStates.Dead)
            .OrderByDescending(i => i.DeadAt).ThenByDescending(i => i.Position)
            .Skip(Math.Max(0, skip)).Take(Math.Max(0, take))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var ids = rows.Select(i => i.EventId).ToList();
        var events = await context.Outbox.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(i => events.TryGetValue(i.EventId, out var e)
            ? new EventDeadLetter(i.EventId, i.Subscriber, e.Type, e.EventVersion, e.RecordType, e.RecordId, i.Attempts, i.LastError, i.DeadAt ?? i.UpdatedAt)
            : new EventDeadLetter(i.EventId, i.Subscriber, "unknown", 0, i.RecordType, i.RecordId, i.Attempts, i.LastError, i.DeadAt ?? i.UpdatedAt))];
    }
}
