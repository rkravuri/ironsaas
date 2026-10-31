using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Jobs.Contracts;
using Iron.Kernel.Jobs.Data;
using Iron.Kernel.Settings.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Jobs.Services;

/// <summary>How the unit's runner works (<c>Jobs:Runner</c>): how often it looks for work, and how many runs it runs at once.</summary>
public sealed class RunnerOptions
{
    public const string SectionName = "Jobs:Runner";

    public int TickMilliseconds { get; set; } = 500;

    /// <summary>
    /// The runs one worker process runs at once; a second process brings its own. The frequent jobs (sealing, dispatch,
    /// re-encryption, the alert rules) each hold one for their minute's term, mostly waiting, so there are room for them and more.
    /// </summary>
    public int Slots { get; set; } = 16;

    /// <summary>How long past its timeout a run's lease lasts, before another worker takes the run as abandoned.</summary>
    public int LeaseGraceSeconds { get; set; } = 30;

    /// <summary>How late an occurrence must be, beyond its interval, before a missed schedule is alerted (TF-123 AC3).</summary>
    public int MissedAfterSeconds { get; set; } = 60;
}

/// <summary>The jobs' spans and metrics: every run is a span of <c>Iron.Jobs</c>, with the tenant's identifier (TF-003 AC2).</summary>
public sealed class JobsTelemetry : IDisposable
{
    public const string SourceName = "Iron.Jobs";

    public static readonly ActivitySource Source = new(SourceName);

    private readonly Meter _meter = new(SourceName);
    private readonly Counter<long> _runs;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _wait;

    public JobsTelemetry()
    {
        _runs = _meter.CreateCounter<long>("jobs.runs", description: "Job runs finished, by job and outcome.");
        _duration = _meter.CreateHistogram<double>("jobs.run_duration", "ms", "How long a run took.");
        _wait = _meter.CreateHistogram<double>("jobs.queue_wait", "ms", "How long a run waited past its time before a worker took it, by priority class.");
    }

    public void Finished(string job, string outcome, double milliseconds)
    {
        _runs.Add(1, new KeyValuePair<string, object?>("job", job), new KeyValuePair<string, object?>("outcome", outcome));
        _duration.Record(milliseconds, new KeyValuePair<string, object?>("job", job));
    }

    public void Waited(PriorityClass priority, TimeSpan wait) =>
        _wait.Record(Math.Max(0, wait.TotalMilliseconds), new KeyValuePair<string, object?>("priority", priority.ToString().ToLowerInvariant()));

    public void Dispose() => _meter.Dispose();
}

/// <summary>What one tick of the runner did.</summary>
public sealed record RunnerTick(int Scheduled, int Claimed, int Recovered, int Skipped, int Held, int Resumed, int Missed)
{
    public bool DidWork => Scheduled + Claimed + Recovered + Skipped + Held + Resumed > 0;
}

/// <summary>
/// The unit's job runner (TF-120 to TF-124, ADR-0061), one per worker process, coordinated through the unit's database
/// so that any number of worker processes run each occurrence of a job once. A tick:
/// <list type="number">
/// <item>at the first tick of the process, makes a record for every declared job and holds the queued runs of suites the
/// unit no longer runs, resuming those of suites it runs again, since a unit's suites are fixed for its process;</item>
/// <item>when the tenant is suspended, records every due occurrence as skipped, logged, and runs nothing (TF-122 AC3);</item>
/// <item>queues each scheduled job's due occurrence, moving its next time on in the same transaction under the job
/// record's row lock, so a second worker skips an occurrence another holds (TF-120 AC1); an occurrence more than one
/// interval late passes over the missed ones and is audited, which raises an alert (TF-123 AC3);</item>
/// <item>under one claim lock, takes back the runs whose holder stopped (their lease passed), then takes queued runs in
/// priority order, interactive before normal before bulk (TF-122 AC2), within each job's concurrency;</item>
/// <item>runs each in a scope of its own, cancelled at its timeout (TF-120 AC2), in a span that carries the tenant and
/// links to the request that started it (TF-003 AC2, TF-130 AC3). A failure is queued for its retry after a growing,
/// jittered wait; after the last attempt it is a dead letter, audited, which raises an alert (TF-123).</item>
/// </list>
/// </summary>
public sealed class JobRunner(
    IServiceScopeFactory scopes,
    JobDeclarations declarations,
    UnitSuites suites,
    RunnerOptions options,
    JobsTelemetry telemetry,
    TimeProvider time,
    ILogger<JobRunner> logger,
    TenantContext? tenant = null)
{
    /// <summary>The lock every worker takes to claim runs, so claims never race and concurrency limits hold across processes.</summary>
    public const long ClaimLockKey = 0x1A_0E_70_B5;

    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private int _settled;

    /// <summary>This process's name in a run's lease: the machine, the process, and a token of this runner.</summary>
    public string Holder { get; } = $"{Environment.MachineName}/{Environment.ProcessId}/{Ids.New().ToString("N")[^8..]}";

    /// <summary>The runs this process is running now.</summary>
    public int Running => _running.Count;

    /// <summary>One tick: the runs it claims go on in the background; <see cref="DrainAsync"/> waits for them.</summary>
    public async Task<RunnerTick> TickAsync(CancellationToken cancellationToken)
    {
        var (held, resumed) = Volatile.Read(ref _settled) == 0 ? await SettleAsync(cancellationToken).ConfigureAwait(false) : (0, 0);
        await using (var scope = scopes.CreateAsyncScope())
        {
            if (scope.ServiceProvider.GetService<ITenantStanding>()?.Suspended == true)
            {
                var skipped = await SkipDueAsync(cancellationToken).ConfigureAwait(false);
                return new RunnerTick(0, 0, 0, skipped, held, resumed, 0);
            }
        }

        var (scheduled, missed) = await ScheduleAsync(cancellationToken).ConfigureAwait(false);
        var (claimed, recovered) = await ClaimAsync(Math.Max(0, options.Slots - _running.Count), cancellationToken).ConfigureAwait(false);
        foreach (var run in claimed)
        {
            var task = Task.Run(() => ExecuteAsync(run, cancellationToken), CancellationToken.None);
            _running[run.Id] = task;
            _ = task.ContinueWith(_ => _running.TryRemove(run.Id, out var _), TaskScheduler.Default);
        }

        return new RunnerTick(scheduled, claimed.Count, recovered, 0, held, resumed, missed);
    }

    /// <summary>Waits for the runs this process started.</summary>
    public Task DrainAsync() => Task.WhenAll(_running.Values);

    /// <summary>One tick and the runs it started, to their end: what a test runs in place of the worker's loop.</summary>
    public async Task<RunnerTick> RunOnceAsync(CancellationToken cancellationToken)
    {
        var tick = await TickAsync(cancellationToken).ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
        return tick;
    }

    /// <summary>
    /// What a build or a suite's removal or return left (ADR-0061): a record for each declared job, scheduled from now; a
    /// queued run of a suite this unit does not run is held, and a held one of a suite it runs again is queued at once.
    /// </summary>
    private async Task<(int Held, int Resumed)> SettleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<JobsDbContext>();
        var zone = await ZoneAsync(services, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        await using var transaction = await services.GetRequiredService<IUnitOfWork>().BeginAsync(cancellationToken).ConfigureAwait(false);
        await JobRecordsSync.EnsureAsync(context, declarations, zone, now, cancellationToken).ConfigureAwait(false);
        var installed = suites.Installed.Select(s => s.ShortName).ToList();
        var toHold = await context.Runs.Where(r => r.Status == RunStates.Queued && r.Suite != null && !installed.Contains(r.Suite)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var toResume = await context.Runs.Where(r => r.Status == RunStates.Held && r.Suite != null && installed.Contains(r.Suite)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var held = toHold.Count(run => run.Hold(now));
        var resumed = toResume.Count(run => run.Resume(now));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _settled, 1);
        if (held + resumed > 0)
        {
            logger.LogInformation("Held {Held} job runs of suites this unit no longer runs, and resumed {Resumed} of suites it runs again.", held, resumed);
        }

        return (held, resumed);
    }

    /// <summary>Every scheduled job's due occurrence, recorded as skipped while the tenant is suspended (TF-122 AC3).</summary>
    private async Task<int> SkipDueAsync(CancellationToken cancellationToken)
    {
        var skipped = 0;
        foreach (var job in declarations.UnitJobs.Where(j => j.IsScheduled))
        {
            skipped += await AdvanceAsync(job, skip: true, cancellationToken).ConfigureAwait(false) is (true, _) ? 1 : 0;
        }

        return skipped;
    }

    private async Task<(int Scheduled, int Missed)> ScheduleAsync(CancellationToken cancellationToken)
    {
        var scheduled = 0;
        var missed = 0;
        foreach (var job in declarations.UnitJobs.Where(j => j.IsScheduled))
        {
            var (queued, passedOver) = await AdvanceAsync(job, skip: false, cancellationToken).ConfigureAwait(false);
            scheduled += queued ? 1 : 0;
            missed += passedOver > 0 ? 1 : 0;
        }

        return (scheduled, missed);
    }

    /// <summary>
    /// The job's due occurrence, queued (or skipped) once: under the record's row lock, which a second worker skips, the
    /// record's next time moves on and the run is added in one transaction. Answers whether it added the run, queued or
    /// skipped, and how many occurrences a late scheduler passed over.
    /// </summary>
    private async Task<(bool Queued, int Missed)> AdvanceAsync(DeclaredJob job, bool skip, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<JobsDbContext>();
        var now = time.GetUtcNow();
        var due = await context.Jobs.AsNoTracking().Where(j => j.Name == job.Name && !j.Paused && j.NextDueAt <= now).Select(j => j.NextDueAt).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (due is null)
        {
            return (false, 0);
        }

        var zone = await ZoneAsync(services, cancellationToken).ConfigureAwait(false);
        await using var transaction = await services.GetRequiredService<IUnitOfWork>().BeginAsync(cancellationToken).ConfigureAwait(false);
        var record = await context.Jobs.FromSql($"select * from kernel_jobs.jobs where name = {job.Name} for update skip locked").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (record is null || record.Paused || record.NextDueAt != due)
        {
            // Another worker holds it, or queued it already.
            return (false, 0);
        }

        var (occurrence, passedOver) = job.Catch(due.Value, now, zone);
        var next = job.NextAfter(occurrence, zone);
        while (next is { } n && n <= now)
        {
            next = job.NextAfter(n, zone);
        }

        record.Advance(next, occurrence);
        var key = occurrence.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var exists = await context.Runs.AnyAsync(r => r.Job == job.Name && r.Key == key, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            var run = new JobRun(job.Name, key, job.Suite, job.Definition.Priority, occurrence, occurrence);
            if (skip)
            {
                run.Skip(now, "Skipped: the tenant is suspended, so its jobs do not run until it is active again.");
                logger.LogWarning("The job {Job} was due at {Occurrence:o} but the tenant is suspended; it was skipped.", job.Name, occurrence);
            }

            context.Runs.Add(run);
        }

        var late = now - due.Value;
        var reportMissed = !skip && passedOver > 0 && late > TimeSpan.FromSeconds(options.MissedAfterSeconds);
        if (reportMissed)
        {
            logger.LogWarning("The job {Job} was due at {Due:o} and is {Late} late: {Missed} occurrence(s) passed over.", job.Name, due.Value, late, passedOver);
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["job"] = job.Name,
                ["due_at"] = due.Value.ToString("O", CultureInfo.InvariantCulture),
                ["missed"] = passedOver.ToString(CultureInfo.InvariantCulture),
                ["late_seconds"] = ((long)late.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            };
            await services.GetRequiredService<IAuditWriter>().WriteAsync(
                new AuditEvent(JobsAuditEvents.ScheduleMissed, AuditOutcome.Failure, SystemPrincipal.SystemId, job.Name, details, JobsRecords.JobRecordType, record.Id, Severity: AuditSeverity.High),
                cancellationToken).ConfigureAwait(false);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (!exists, reportMissed ? passedOver : 0);
    }

    /// <summary>
    /// Under the claim lock: the runs whose holder's lease passed go back to the queue (or become dead letters after their
    /// last attempt); then queued runs whose time came are taken in priority order, oldest first within a class, within
    /// each job's concurrency, as many as this process has slots for.
    /// </summary>
    private async Task<(IReadOnlyList<JobRun> Claimed, int Recovered)> ClaimAsync(int slots, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<JobsDbContext>();
        await using var transaction = await services.GetRequiredService<IUnitOfWork>().BeginAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({ClaimLockKey})", cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();

        var abandoned = await context.Runs.Where(r => r.Status == RunStates.Running && r.LeaseUntil < now).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var run in abandoned)
        {
            logger.LogWarning("The run {RunId} of {Job} was held by {Holder}, which stopped before it finished; it is taken back.", run.Id, run.Job, run.Holder);
            await FailAsync(services, run, "The worker running it stopped before it finished.", run.Log, now, cancellationToken).ConfigureAwait(false);
        }

        var claimed = new List<JobRun>();
        if (slots > 0)
        {
            var paused = await context.Jobs.AsNoTracking().Where(j => j.Paused).Select(j => j.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
            var running = await context.Runs.AsNoTracking().Where(r => r.Status == RunStates.Running).GroupBy(r => r.Job).Select(g => new { Job = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Job, g => g.Count, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
            var queued = await context.Runs.Where(r => r.Status == RunStates.Queued && r.DueAt <= now && !paused.Contains(r.Job))
                .OrderBy(r => r.Priority).ThenBy(r => r.DueAt).ThenBy(r => r.Id)
                .Take((slots * 4) + 50)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var run in queued)
            {
                if (claimed.Count >= slots)
                {
                    break;
                }

                var job = declarations.Find(run.Job);
                if (job is null || job.Definition.Scope != JobScope.Unit)
                {
                    if (run.Suite is { } suite && suites.Find(suite) is null)
                    {
                        run.Hold(now);
                    }
                    else
                    {
                        // A Foundation job this build no longer declares: a programmer's error the operator must see.
                        run.Claim(Holder, now, now);
                        await FailAsync(services, run, $"{JobsErrors.JobNotFound}: no module of this unit declares the job {run.Job}.", string.Empty, now, cancellationToken, dead: true).ConfigureAwait(false);
                    }

                    continue;
                }

                var count = running.GetValueOrDefault(run.Job);
                if (count >= job.Definition.MaxParallel)
                {
                    continue;
                }

                running[run.Job] = count + 1;
                telemetry.Waited(job.Definition.Priority, now - run.DueAt);
                run.Claim(Holder, now, now + job.Definition.Timeout + TimeSpan.FromSeconds(options.LeaseGraceSeconds));
                claimed.Add(run);
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (claimed, abandoned.Count);
    }

    /// <summary>
    /// One run, in a scope and a span of its own, cancelled at its timeout (TF-120 AC2). The span carries the tenant and
    /// links to the request that started the run (TF-003 AC2, TF-130 AC3); the run's database is the unit's, its tenant's
    /// only, since a unit's processes hold no other connection.
    /// </summary>
    private async Task ExecuteAsync(JobRun run, CancellationToken stoppingToken)
    {
        var job = declarations.Find(run.Job)!;
        var links = run.TraceParent is { } parent && ActivityContext.TryParse(parent, null, out var origin) ? new[] { new ActivityLink(origin) } : null;
        using var activity = JobsTelemetry.Source.StartActivity($"job {run.Job}", ActivityKind.Internal, parentContext: default, links: links);
        activity?.SetTag("tenant.id", tenant?.Id.Value);
        activity?.SetTag("job.name", run.Job);
        activity?.SetTag("job.run_id", run.Id.ToString("D"));
        activity?.SetTag("job.attempt", run.Attempts);
        activity?.SetTag("job.priority", job.Definition.Priority.ToString().ToLowerInvariant());
        using var logScope = logger.BeginScope(new Dictionary<string, object?> { ["Job"] = run.Job, ["RunId"] = run.Id, ["TenantId"] = tenant?.Id.Value });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(job.Definition.Timeout);
        var context = new JobContext(run.Id, run.Job, run.Key, run.Attempts, run.ScheduledFor, run.Payload, run.TriggeredBy);
        var started = time.GetUtcNow();
        string? error;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = (IJob)scope.ServiceProvider.GetRequiredService(job.Definition.HandlerType);
            var result = await handler.RunAsync(context, timeout.Token).ConfigureAwait(false);
            error = result.IsSuccess ? null : $"{result.Error.Code}: {result.Error.Message}";
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The process is stopping: the run keeps its lease, and the next worker takes it back when the lease passes.
            logger.LogInformation("The run {RunId} of {Job} stopped with its process; another worker takes it back.", run.Id, run.Job);
            return;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            error = $"Timed out after {JobSchedule.Words(job.Definition.Timeout)}: the run was cancelled.";
            context.Log(error);
            logger.LogWarning("The run {RunId} of {Job} passed its timeout of {Timeout} and was cancelled.", run.Id, run.Job, job.Definition.Timeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";
            context.Log(error);
            logger.LogWarning(exception, "The run {RunId} of {Job} failed.", run.Id, run.Job);
        }

        activity?.SetStatus(error is null ? ActivityStatusCode.Ok : ActivityStatusCode.Error, error);
        telemetry.Finished(run.Job, error is null ? "succeeded" : "failed", (time.GetUtcNow() - started).TotalMilliseconds);
        try
        {
            await RecordAsync(run.Id, error, context.Text).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The run keeps its lease; when it passes, a worker takes the run back and runs it again, which is safe since a job is idempotent.
            logger.LogError(exception, "The outcome of the run {RunId} of {Job} could not be recorded; it is taken back when its lease passes.", run.Id, run.Job);
        }
    }

    /// <summary>The run's outcome, in a transaction of its own; a run whose lease another worker took back is left to it.</summary>
    private async Task RecordAsync(Guid runId, string? error, string log)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<JobsDbContext>();
        await using var transaction = await services.GetRequiredService<IUnitOfWork>().BeginAsync(deadline.Token).ConfigureAwait(false);
        var run = await context.Runs.FromSql($"select * from kernel_jobs.runs where id = {runId} for update").SingleOrDefaultAsync(deadline.Token).ConfigureAwait(false);
        if (run is null || run.Status != RunStates.Running || run.Holder != Holder)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (error is null)
        {
            run.Succeeded(now, log);
        }
        else
        {
            await FailAsync(services, run, error, log, now, deadline.Token).ConfigureAwait(false);
        }

        await context.SaveChangesAsync(deadline.Token).ConfigureAwait(false);
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// A failed attempt: queued again after the policy's growing, jittered wait (TF-123 AC1), or after the last attempt a
    /// dead letter with its error, audited in the same transaction, which raises an alert (TF-123 AC2).
    /// </summary>
    private async Task FailAsync(IServiceProvider services, JobRun run, string error, string log, DateTimeOffset now, CancellationToken cancellationToken, bool dead = false)
    {
        var policy = declarations.Find(run.Job)?.Definition.Retry ?? RetryPolicy.Default;
        var last = dead || run.Attempts >= policy.Attempts;
        run.Failed(now, error, log, last ? null : now + policy.DelayAfter(run.Attempts, RetryPolicy.Jitter()));
        if (!last)
        {
            return;
        }

        logger.LogError("The run {RunId} of {Job} failed {Attempts} time(s); it is a dead letter now: {Error}", run.Id, run.Job, run.Attempts, error);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["job"] = run.Job,
            ["key"] = run.Key,
            ["attempts"] = run.Attempts.ToString(CultureInfo.InvariantCulture),
        };
        await services.GetRequiredService<IAuditWriter>().WriteAsync(
            new AuditEvent(JobsAuditEvents.RunDeadLettered, AuditOutcome.Failure, SystemPrincipal.SystemId, run.Job, details, JobsRecords.RunRecordType, run.Id, run.Error, Severity: AuditSeverity.High),
            cancellationToken).ConfigureAwait(false);
    }

    private static Task<TimeZoneInfo> ZoneAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        JobZone.OfAsync(services.GetService<ISettings>(), cancellationToken);
}

/// <summary>
/// A record for every job a unit declares (TF-124 AC1), active and scheduled from now; a record whose job turned from
/// explicit to scheduled, or back, starts over. Under one lock, in the caller's transaction, so the seed and two workers
/// starting at once never make a record twice.
/// </summary>
public static class JobRecordsSync
{
    public const long LockKey = 0x1A_0E_70_B6;

    public static async Task EnsureAsync(JobsDbContext context, JobDeclarations declarations, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(declarations);
        await context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({LockKey})", cancellationToken).ConfigureAwait(false);
        var records = await context.Jobs.ToDictionaryAsync(j => j.Name, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        foreach (var job in declarations.UnitJobs)
        {
            if (!records.TryGetValue(job.Name, out var record))
            {
                context.Jobs.Add(new JobRecord(job.Name, job.Suite, job.FirstDue(now, zone)));
            }
            else if (job.IsScheduled != record.NextDueAt.HasValue && !record.Paused)
            {
                record.Reschedule(job.FirstDue(now, zone));
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The record types a job and a run are in the audit trail.</summary>
public static class JobsRecords
{
    public const string JobRecordType = "job";
    public const string RunRecordType = "job_run";
}

/// <summary>The runner's loop in a worker: a tick, then another at once while there is work, otherwise after the tick's interval.</summary>
public sealed class JobRunnerService(JobRunner runner, RunnerOptions options, TimeProvider time, ILogger<JobRunnerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(50, options.TickMilliseconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                worked = (await runner.TickAsync(stoppingToken).ConfigureAwait(false)).DidWork;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "A tick of the job runner failed; it runs again in {Interval}.", interval);
            }

            try
            {
                if (!worked)
                {
                    await Task.Delay(interval, time, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Runs still going when the process stops keep their lease; the next worker takes them back.
        await Task.WhenAny(runner.DrainAsync(), Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(false);
    }
}
