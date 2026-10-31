using System.Diagnostics.Metrics;
using System.Globalization;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Events.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Events.Services;

/// <summary>How the dispatcher runs (<c>Events:Dispatch</c>, ADR-0060): how often it looks when idle, how much it takes at a time, how many deliveries run at once, and the retries.</summary>
public sealed class DispatchOptions
{
    public const string SectionName = "Events:Dispatch";

    /// <summary>How long an idle dispatcher waits before it looks again; a pass that found work looks again at once.</summary>
    public int IntervalMilliseconds { get; set; } = 200;

    /// <summary>How many events a pass hands on, and how many deliveries it takes.</summary>
    public int Batch { get; set; } = 200;

    /// <summary>How many deliveries run at once, each about a different record.</summary>
    public int Parallelism { get; set; } = 4;

    /// <summary>How many times a subscriber tries an event before it is parked as a dead letter (TF-085 AC2).</summary>
    public int Attempts { get; set; } = 6;

    /// <summary>The wait after the first failure; it doubles after each, up to <see cref="LongestRetrySeconds"/>.</summary>
    public int FirstRetrySeconds { get; set; } = 2;

    public int LongestRetrySeconds { get; set; } = 600;

    /// <summary>The wait after the given failed attempt, counting from one.</summary>
    public TimeSpan RetryAfter(int failedAttempts)
    {
        var seconds = FirstRetrySeconds * Math.Pow(2, Math.Max(0, failedAttempts - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, LongestRetrySeconds));
    }
}

/// <summary>
/// The events meter (ADR-0060): <c>events.handled_latency</c>, the milliseconds from an event's commit to its handling by
/// a subscriber, which TF-085 AC1 bounds at ten seconds; <c>events.dead_letters</c>, the events parked, by subscriber.
/// </summary>
public sealed class EventsMetrics : IDisposable
{
    public const string MeterName = "Iron.Events";

    private readonly Meter _meter = new(MeterName);
    private readonly Histogram<double> _latency;
    private readonly Counter<long> _dead;

    public EventsMetrics()
    {
        _latency = _meter.CreateHistogram<double>("events.handled_latency", unit: "ms", description: "From an event's commit to its handling by a subscriber.");
        _dead = _meter.CreateCounter<long>("events.dead_letters", description: "Events parked as dead letters, by subscriber.");
    }

    public void Handled(string subscriber, TimeSpan latency) => _latency.Record(latency.TotalMilliseconds, new KeyValuePair<string, object?>("subscriber", subscriber));

    public void DeadLettered(string subscriber) => _dead.Add(1, new KeyValuePair<string, object?>("subscriber", subscriber));

    public void Dispose() => _meter.Dispose();
}

/// <summary>What one pass of the dispatcher did; the first pass also holds a removed suite's deliveries and resumes a returned one's.</summary>
public sealed record DispatchPass(int HandedOn, int Handled, int Failed, int DeadLettered, int AlreadyHandled, int Held = 0, int Resumed = 0)
{
    public bool DidWork => HandedOn + Handled + Failed + DeadLettered + AlreadyHandled + Held + Resumed > 0;
}

/// <summary>
/// The in-process dispatcher (TF-085, TF-111, ADR-0060), one per unit, in its worker. A pass first hands the events it has
/// not handed on yet to their subscribers, writing each one's row in the inbox; then it delivers what is due, in the order
/// the events were written per record and subscriber, different records at once. A delivery runs the handler in a unit of
/// work that also marks the row handled, so what the handler did and that it did it commit together; a repeated delivery
/// finds the row handled and changes nothing. A failure rolls the handler's work back, counts an attempt and waits longer
/// each time; after the last, the row is a dead letter with its error, audited, which the alert rules raise. One
/// subscriber's failures never hold the others up, nor its other records. A broker could replace the hand-on later
/// without a change to publishers or handlers (TF-113): the outbox stays the source, the inbox the record of what is done.
/// A delivery to a subscriber of a suite the unit no longer runs is held, neither tried nor alerted, and resumes by itself
/// when the suite returns: the suites a unit runs are fixed for its process, so the first pass settles both.
/// </summary>
public sealed class EventDispatcher(
    IServiceScopeFactory scopes,
    EventDeclarations declarations,
    UnitSuites suites,
    DispatchOptions options,
    EventsMetrics metrics,
    TimeProvider time,
    ILogger<EventDispatcher> logger)
{
    /// <summary>The advisory lock a pass's hand-on takes, so two workers never hand the same event on twice.</summary>
    public const long HandOnLockKey = 0x1A_0E_7E_47;

    private int _suitesSettled;

    public async Task<DispatchPass> RunOnceAsync(CancellationToken cancellationToken)
    {
        var (held, resumed) = Volatile.Read(ref _suitesSettled) == 0 ? await SettleSuitesAsync(cancellationToken).ConfigureAwait(false) : (0, 0);
        var handedOn = await HandOnAsync(cancellationToken).ConfigureAwait(false);
        var due = await DueAsync(cancellationToken).ConfigureAwait(false);
        using var gate = new SemaphoreSlim(Math.Max(1, options.Parallelism));
        var outcomes = await Task.WhenAll(due.Select(async delivery =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await DeliverAsync(delivery, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return new DispatchPass(
            handedOn,
            outcomes.Count(o => o == DeliveryOutcome.Handled),
            outcomes.Count(o => o == DeliveryOutcome.Failed),
            outcomes.Count(o => o == DeliveryOutcome.DeadLettered),
            outcomes.Count(o => o == DeliveryOutcome.AlreadyHandled),
            held + outcomes.Count(o => o == DeliveryOutcome.Held),
            resumed);
    }

    /// <summary>
    /// What a suite's removal or return left in the inbox (ADR-0060): a pending delivery to a subscriber of a suite this unit
    /// does not run is held, with no attempt, no error, no audit event and no alert; a held one whose suite this unit runs
    /// again is pending at once, in the order of its positions. Run once per process, at its first pass.
    /// </summary>
    private async Task<(int Held, int Resumed)> SettleSuitesAsync(CancellationToken cancellationToken)
    {
        var installed = suites.Installed.Select(suite => suite.ShortName).ToList();
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
        var now = time.GetUtcNow();
        var toHold = await context.Inbox
            .Where(i => i.Status == DeliveryStates.Pending && i.Suite != null && !installed.Contains(i.Suite))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var toResume = await context.Inbox
            .Where(i => i.Status == DeliveryStates.Held && i.Suite != null && installed.Contains(i.Suite))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var held = toHold.Count(entry => entry.Hold(now));
        var resumed = toResume.Count(entry => entry.Resume(now));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _suitesSettled, 1);
        if (held + resumed > 0)
        {
            logger.LogInformation("Held {Held} deliveries of suites this unit no longer runs, and resumed {Resumed} of suites it runs again.", held, resumed);
        }

        return (held, resumed);
    }

    /// <summary>
    /// One inbox entry delivered now, as a pass delivers each due one: its handler runs, unless the entry is handled
    /// already, which a repeated delivery finds (TF-085 AC3).
    /// </summary>
    public async Task<DeliveryOutcome> DeliverAsync(Guid inboxEntryId, CancellationToken cancellationToken)
    {
        Due? due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<EventsDbContext>().Inbox.AsNoTracking()
                .Where(i => i.Id == inboxEntryId)
                .Select(i => new Due(i.Id, i.Subscriber, i.EventId))
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return due is null ? DeliveryOutcome.AlreadyHandled : await DeliverAsync(due, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every event not handed on yet gets a row in the inbox for each subscription that reads it; one with none is handed on to nobody. Answers how many it handed on.</summary>
    public async Task<int> HandOnAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var locked = await context.Database.SqlQuery<bool>($"select pg_try_advisory_xact_lock({HandOnLockKey}) as \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);
        if (!locked)
        {
            return 0;
        }

        var waiting = await context.Outbox.Where(e => e.FannedAt == null).OrderBy(e => e.Position).Take(options.Batch).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (waiting.Count == 0)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        foreach (var outboxEvent in waiting)
        {
            foreach (var subscription in declarations.SubscribersOf(outboxEvent.Type, outboxEvent.EventVersion))
            {
                context.Inbox.Add(new InboxEntry(subscription.Name, outboxEvent.Id, outboxEvent.Position, outboxEvent.RecordType, outboxEvent.RecordId, now, declarations.SuiteOf(subscription.Name)));
            }

            outboxEvent.Fan(now);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return waiting.Count;
    }

    /// <summary>
    /// What is due: pending rows whose time has come, each the earliest one of its subscriber and record still pending, so
    /// a later event of a record waits while an earlier one is retried (TF-085: in order per record). A dead letter holds
    /// nothing back. Different records are due at once.
    /// </summary>
    private async Task<IReadOnlyList<Due>> DueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
        var now = time.GetUtcNow();
        return await context.Inbox.AsNoTracking()
            .Where(i => i.Status == DeliveryStates.Pending && i.NextAttemptAt <= now)
            .Where(i => !context.Inbox.Any(earlier => earlier.Subscriber == i.Subscriber
                && earlier.RecordType == i.RecordType
                && earlier.RecordId == i.RecordId
                && earlier.Status == DeliveryStates.Pending
                && earlier.Position < i.Position))
            .OrderBy(i => i.Position)
            .Take(options.Batch)
            .Select(i => new Due(i.Id, i.Subscriber, i.EventId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DeliveryOutcome> DeliverAsync(Due due, CancellationToken cancellationToken)
    {
        string error;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var context = services.GetRequiredService<EventsDbContext>();
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
            var entry = await LockAsync(context, due.Id, cancellationToken).ConfigureAwait(false);
            if (entry is null || entry.Status != DeliveryStates.Pending)
            {
                // Handled already, by an earlier delivery or another worker: a repeated delivery changes nothing (TF-085 AC3).
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DeliveryOutcome.AlreadyHandled;
            }

            var outboxEvent = await context.Outbox.AsNoTracking().SingleOrDefaultAsync(e => e.Id == due.EventId, cancellationToken).ConfigureAwait(false);
            var subscription = declarations.FindSubscription(due.Subscriber);
            if (subscription is null && entry.Suite is { } suite && suites.Find(suite) is null)
            {
                // A subscriber of a suite this unit no longer runs: held until the suite returns, never a dead letter.
                entry.Hold(time.GetUtcNow());
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DeliveryOutcome.Held;
            }

            if (outboxEvent is null || subscription is null)
            {
                error = outboxEvent is null ? "events.event.not_found: the event is no longer in the outbox." : $"events.subscriber.unknown: no module of this unit holds the subscription {due.Subscriber}.";
            }
            else
            {
                try
                {
                    var result = await subscription.InvokeAsync(services, outboxEvent.ToEnvelope(), cancellationToken).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        var now = time.GetUtcNow();
                        entry.Handled(now);
                        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        metrics.Handled(due.Subscriber, now - outboxEvent.OccurredAt);
                        return DeliveryOutcome.Handled;
                    }

                    error = $"{result.Error.Code}: {result.Error.Message}";
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "The subscriber {Subscriber} failed the event {EventId}; it is retried.", due.Subscriber, due.EventId);
                    error = $"{exception.GetType().Name}: {exception.Message}";
                }
            }

            // Leaving without a commit rolls back whatever the handler did.
        }

        return await RecordFailureAsync(due, error, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The attempt counted in a transaction of its own, after the handler's was rolled back.</summary>
    private async Task<DeliveryOutcome> RecordFailureAsync(Due due, string error, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<EventsDbContext>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var entry = await LockAsync(context, due.Id, cancellationToken).ConfigureAwait(false);
        if (entry is null || entry.Status != DeliveryStates.Pending)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DeliveryOutcome.AlreadyHandled;
        }

        var now = time.GetUtcNow();
        var last = entry.Attempts + 1 >= options.Attempts;
        entry.Failed(now, error, last ? null : now + options.RetryAfter(entry.Attempts + 1));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (last)
        {
            var outboxEvent = await context.Outbox.AsNoTracking().Where(e => e.Id == due.EventId).Select(e => new { e.Type, e.EventVersion }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["subscriber"] = due.Subscriber,
                ["event_type"] = outboxEvent?.Type ?? "unknown",
                ["event_version"] = (outboxEvent?.EventVersion ?? 0).ToString(CultureInfo.InvariantCulture),
                ["attempts"] = entry.Attempts.ToString(CultureInfo.InvariantCulture),
            };
            await services.GetRequiredService<IAuditWriter>().WriteAsync(
                new AuditEvent(EventsAuditEvents.DeliveryDeadLettered, AuditOutcome.Failure, SystemPrincipal.SystemId, due.Subscriber, details, EventRecords.RecordType, due.EventId, entry.LastError, Severity: AuditSeverity.High),
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (last)
        {
            metrics.DeadLettered(due.Subscriber);
            logger.LogError("The subscriber {Subscriber} failed the event {EventId} {Attempts} times; it is a dead letter now.", due.Subscriber, due.EventId, entry.Attempts);
            return DeliveryOutcome.DeadLettered;
        }

        return DeliveryOutcome.Failed;
    }

    /// <summary>The inbox row, locked for the transaction, so two deliveries of one event never run its handler at once.</summary>
    private static Task<InboxEntry?> LockAsync(EventsDbContext context, Guid id, CancellationToken cancellationToken) =>
        context.Inbox.FromSql($"select * from kernel_events.inbox where id = {id} for update").SingleOrDefaultAsync(cancellationToken);

    private sealed record Due(Guid Id, string Subscriber, Guid EventId);
}

/// <summary>
/// What a delivery did: the handler ran and committed, failed and waits for its retry, failed its last attempt, found the
/// entry handled already, or held it because its subscriber's suite is no longer run.
/// </summary>
public enum DeliveryOutcome
{
    Handled,
    Failed,
    DeadLettered,
    AlreadyHandled,
    Held,
}

/// <summary>The record type an event is in the audit trail and the history: a dead letter, a replay, an archive name it.</summary>
public static class EventRecords
{
    public const string RecordType = "domain_event";
}

/// <summary>
/// <c>events.dispatch</c> (TF-085, TF-111, TF-124, ADR-0060, ADR-0061): the unit's dispatcher, as a run a minute that
/// passes every <c>Events:Dispatch:IntervalMilliseconds</c> (200 ms), and at once again while a pass found work, so an
/// event is handed on within a second of its commit. Nothing the outbox holds is lost by a crash, since the next run,
/// on any worker, finds it there (TF-085).
/// </summary>
public sealed class EventDispatchJob(EventDispatcher dispatcher, DispatchOptions options, TimeProvider time) : IJob
{
    public static readonly JobDefinition Definition = JobDefinition.Create<EventDispatchJob>(
        "events.dispatch", "Hands each committed domain event to its subscribers within a second, retrying failures and parking dead letters.", "Platform team",
        JobSchedule.Interval(TimeSpan.FromMinutes(1)), PriorityClass.Interactive, TimeSpan.FromMinutes(2));

    public async Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var handled = 0;
        var dead = 0;
        var result = await JobLoop.RunAsync(context, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(Math.Max(10, options.IntervalMilliseconds)), time, async ct =>
        {
            var pass = await dispatcher.RunOnceAsync(ct).ConfigureAwait(false);
            handled += pass.Handled;
            dead += pass.DeadLettered;
            return pass.DidWork;
        }, cancellationToken).ConfigureAwait(false);
        context.Log($"{handled} deliveries handled, {dead} dead-lettered.");
        return result;
    }
}

/// <summary>
/// The control plane's dispatch loop, in the fleet (ADR-0060): the control plane runs no job runner, so its own events
/// are handed on here, a pass, then another at once while there is work, otherwise after the interval. A unit's worker
/// hands its events on through <see cref="EventDispatchJob"/>.
/// </summary>
public sealed class EventDispatchWorker(EventDispatcher dispatcher, DispatchOptions options, TimeProvider time, ILogger<EventDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(10, options.IntervalMilliseconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                worked = (await dispatcher.RunOnceAsync(stoppingToken).ConfigureAwait(false)).DidWork;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "A pass of the event dispatcher failed; it runs again in {Interval}.", interval);
            }

            if (!worked)
            {
                try
                {
                    await Task.Delay(interval, time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
