using System.Diagnostics;
using System.Globalization;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Jobs.Contracts;
using Iron.Kernel.Jobs.Data;
using Iron.Kernel.Settings.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Jobs.Services;

/// <summary>The tenant's time zone for cron schedules (<c>jobs.time_zone</c>, ADR-0061); UTC when it names none this host knows.</summary>
public static class JobZone
{
    public static async Task<TimeZoneInfo> OfAsync(ISettings? settings, CancellationToken cancellationToken)
    {
        var name = settings is null ? "UTC" : await settings.GetAsync(JobsSettings.TimeZone, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        return TimeZoneInfo.TryFindSystemTimeZoneById(string.IsNullOrWhiteSpace(name) ? "UTC" : name, out var zone) ? zone : TimeZoneInfo.Utc;
    }
}

/// <summary>
/// Starts runs for code (ADR-0061): an explicit job, or a scheduled one now, keyed so a second start with the same key is
/// the same run. In the caller's unit of work the run commits with its change; the request's trace goes with it, so the
/// run's span links to the request (TF-130 AC3).
/// </summary>
public sealed class JobStarter(JobsDbContext context, JobDeclarations declarations, ICurrentPrincipal actor, TimeProvider time) : IJobStarter
{
    public async Task<Guid> StartAsync(JobDefinition job, string key, string? payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var declared = declarations.Find(job.Name) ?? throw new InvalidOperationException($"No module of this host declares the job '{job.Name}'.");
        if (declared.Definition.Scope != JobScope.Unit)
        {
            throw new InvalidOperationException($"The job '{job.Name}' runs in each process; it has no run to start.");
        }

        var existing = await context.Runs.Where(r => r.Job == job.Name && r.Key == key).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is { } id)
        {
            return id;
        }

        var now = time.GetUtcNow();
        var by = actor.Id == SystemPrincipal.SystemId ? (Guid?)null : actor.Id;
        var run = new JobRun(job.Name, key, declared.Suite, job.Priority, now, now, by, payload: payload, traceParent: Activity.Current?.Id);
        context.Runs.Add(run);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Nothing here changes the run again, and a starter that queues many runs in one scope stays as quick for the last.
        context.Entry(run).State = EntityState.Detached;
        return run.Id;
    }
}

/// <summary>A run as the console lists it.</summary>
public sealed record RunView(
    Guid Id,
    string Job,
    string Key,
    string Status,
    PriorityClass Priority,
    int Attempts,
    DateTimeOffset ScheduledFor,
    DateTimeOffset DueAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int? DurationMs,
    string? Error,
    Guid? TriggeredBy,
    string? Reason,
    DateTimeOffset? DeadAt,
    int Reruns,
    string? Log)
{
    public static RunView Of(JobRun run, bool withLog)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new RunView(run.Id, run.Job, run.Key, run.Status, (PriorityClass)run.Priority, run.Attempts, run.ScheduledFor, run.DueAt, run.StartedAt, run.FinishedAt, run.DurationMs, run.Error, run.TriggeredBy, run.Reason, run.DeadAt, run.Reruns, withLog ? run.Log : null);
    }
}

/// <summary>
/// A job as the console shows it (TF-121): what it is and how it runs; its state (active, paused, held while its suite is
/// removed, or run in each process); its next run; its last run and last failure; its typical duration; and its backlog:
/// the runs waiting, running, and parked as dead letters.
/// </summary>
public sealed record JobView(
    string Name,
    string Module,
    string? Suite,
    string Description,
    string Owner,
    string Schedule,
    JobScope Scope,
    PriorityClass Priority,
    int TimeoutSeconds,
    int Attempts,
    int MaxParallel,
    string UniqueBy,
    string State,
    DateTimeOffset? PausedAt,
    Guid? PausedBy,
    string? PauseReason,
    DateTimeOffset? NextRunAt,
    RunView? LastRun,
    RunView? LastFailure,
    int? TypicalDurationMs,
    int Queued,
    int Running,
    int DeadLetters);

/// <summary>A dead letter of a job or of an event subscription, as the console's one view lists them (ADR-0061).</summary>
public sealed record DeadLetterView(string Kind, Guid Id, string Name, string What, int Attempts, string? Error, DateTimeOffset DeadAt);

public static class JobStates
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Held = "held";
    public const string EachProcess = "each_process";
}

/// <summary>
/// What an administrator, and the operator through the unit's command, see and do with the unit's jobs (TF-121,
/// ADR-0061): every job with its runs, logs and backlog; pause, resume, run now, and run a dead letter again, each with a
/// reason and audited, the operator's in the operator's name; and one view of the dead letters of jobs and events.
/// </summary>
public sealed class JobConsole(
    JobsDbContext context,
    JobDeclarations declarations,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    TimeProvider time,
    IEventDeadLetters? eventDeadLetters = null,
    ISettings? settings = null)
{
    public const int ReasonMaxLength = JobRun.ReasonMaxLength;

    public async Task<IReadOnlyList<JobView>> ListAsync(CancellationToken cancellationToken)
    {
        var records = await context.Jobs.AsNoTracking().ToDictionaryAsync(j => j.Name, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var counts = await context.Runs.AsNoTracking()
            .Where(r => r.Status == RunStates.Queued || r.Status == RunStates.Running || r.Status == RunStates.Dead || r.Status == RunStates.Held)
            .GroupBy(r => new { r.Job, r.Status })
            .Select(g => new { g.Key.Job, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var last = await context.Runs.AsNoTracking().Where(r => r.FinishedAt != null)
            .GroupBy(r => r.Job).Select(g => g.OrderByDescending(r => r.FinishedAt).First())
            .ToDictionaryAsync(r => r.Job, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var failures = await context.Runs.AsNoTracking().Where(r => r.Error != null && r.FinishedAt != null)
            .GroupBy(r => r.Job).Select(g => g.OrderByDescending(r => r.FinishedAt).First())
            .ToDictionaryAsync(r => r.Job, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var since = time.GetUtcNow().AddDays(-1);
        var durations = await context.Runs.AsNoTracking().Where(r => r.Status == RunStates.Succeeded && r.DurationMs != null && r.FinishedAt > since)
            .GroupBy(r => r.Job).Select(g => new { Job = g.Key, Average = g.Average(r => (double)r.DurationMs!.Value) })
            .ToDictionaryAsync(g => g.Job, g => (int)Math.Round(g.Average), StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);

        int Count(string job, string status) => counts.Where(c => c.Job == job && c.Status == status).Sum(c => c.Count);

        var views = new List<JobView>();
        foreach (var job in declarations.Jobs)
        {
            var definition = job.Definition;
            records.TryGetValue(job.Name, out var record);
            var state = definition.Scope == JobScope.EachProcess ? JobStates.EachProcess : record?.Paused == true ? JobStates.Paused : JobStates.Active;
            views.Add(new JobView(
                job.Name, definition.Module, job.Suite, definition.Description, definition.Owner, job.Describe(), definition.Scope, definition.Priority,
                (int)definition.Timeout.TotalSeconds, definition.Retry.Attempts, definition.MaxParallel, definition.UniqueBy,
                state, record?.PausedAt, record?.PausedBy, record?.PauseReason, record?.Paused == true ? null : record?.NextDueAt,
                last.TryGetValue(job.Name, out var lastRun) ? RunView.Of(lastRun, false) : null,
                failures.TryGetValue(job.Name, out var failure) ? RunView.Of(failure, false) : null,
                durations.TryGetValue(job.Name, out var typical) ? typical : null,
                Count(job.Name, RunStates.Queued), Count(job.Name, RunStates.Running), Count(job.Name, RunStates.Dead)));
        }

        // A job of a suite this unit no longer runs: its record and runs stay, held, until the suite returns.
        foreach (var record in records.Values.Where(r => declarations.Find(r.Name) is null))
        {
            views.Add(new JobView(
                record.Name, record.Name[..record.Name.IndexOf('.', StringComparison.Ordinal)], record.Suite, "A job of a suite this unit no longer runs.", "-", "-", JobScope.Unit, PriorityClass.Normal,
                0, 0, 1, "-", JobStates.Held, null, null, null, null,
                last.TryGetValue(record.Name, out var lastRun) ? RunView.Of(lastRun, false) : null,
                failures.TryGetValue(record.Name, out var failure) ? RunView.Of(failure, false) : null,
                null, Count(record.Name, RunStates.Held), 0, Count(record.Name, RunStates.Dead)));
        }

        return [.. views.OrderBy(v => v.Name, StringComparer.Ordinal)];
    }

    public async Task<Result<JobView>> GetAsync(string name, CancellationToken cancellationToken)
    {
        var view = (await ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(v => v.Name == name);
        return view is null ? Error.NotFound(JobsErrors.JobNotFound, $"There is no job '{name}' in this unit.") : view;
    }

    public async Task<Result<RunView>> RunAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await context.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        return run is null ? Error.NotFound(JobsErrors.RunNotFound, "There is no such job run in this unit.") : RunView.Of(run, true);
    }

    /// <summary>Paused (TF-121 AC2): the job queues no run until it is resumed, and its waiting runs wait; what runs now finishes.</summary>
    public Task<Result<JobView>> PauseAsync(string name, string? reason, string? operatorName, CancellationToken cancellationToken) =>
        ChangeAsync(name, reason, operatorName, JobsAuditEvents.JobPaused, (record, why, now) => record.Pause(actor.Id, why, now)
            ? Result.Success()
            : Error.Conflict(JobsErrors.AlreadyPaused, $"The job '{name}' is paused already."), cancellationToken);

    /// <summary>Resumed: a scheduled job falls due next by its schedule from now, so the time it was paused is not a missed schedule.</summary>
    public async Task<Result<JobView>> ResumeAsync(string name, string? reason, string? operatorName, CancellationToken cancellationToken)
    {
        DateTimeOffset? next = null;
        if (declarations.Find(name) is { IsScheduled: true } job)
        {
            var now = time.GetUtcNow();
            next = job.Definition.Schedule.Kind == JobScheduleKind.Interval ? now + job.Every!.Value : job.NextAfter(now, await JobZone.OfAsync(settings, cancellationToken).ConfigureAwait(false));
        }

        return await ChangeAsync(name, reason, operatorName, JobsAuditEvents.JobResumed, (record, _, _) => record.Resume(next)
            ? Result.Success()
            : Error.Conflict(JobsErrors.NotPaused, $"The job '{name}' is not paused."), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A run now, at the job's priority, with the reason in its record and the trail (TF-121); the request's trace goes with it.</summary>
    public async Task<Result<RunView>> TriggerAsync(string name, string? reason, string? operatorName, CancellationToken cancellationToken)
    {
        var why = reason?.Trim();
        var job = declarations.Find(name);
        var refused = Refusal(name, job, why);
        if (refused is not null)
        {
            return refused;
        }

        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var run = new JobRun(name, $"trigger:{Ids.New():N}", job!.Suite, job.Definition.Priority, now, now, actor.Id, why, traceParent: Activity.Current?.Id);
        context.Runs.Add(run);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(JobsAuditEvents.RunTriggered, name, JobsRecords.RunRecordType, run.Id, why!, operatorName, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RunView.Of(run, false);
    }

    /// <summary>A dead letter run again (TF-121): queued now with its attempts from nothing; a run that is not a dead letter is refused.</summary>
    public async Task<Result<RunView>> RerunAsync(Guid runId, string? reason, string? operatorName, CancellationToken cancellationToken)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why))
        {
            return ReasonRequired();
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var run = await context.Runs.FromSql($"select * from kernel_jobs.runs where id = {runId} for update").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Error.NotFound(JobsErrors.RunNotFound, "There is no such job run in this unit.");
        }

        if (declarations.Find(run.Job) is null)
        {
            return Error.Conflict(JobsErrors.JobHeld, $"The job '{run.Job}' is not run in this unit now; its suite was removed. Its runs wait until the suite is added again.");
        }

        if (!run.Rerun(time.GetUtcNow()))
        {
            return Error.Conflict(JobsErrors.RunNotFailed, $"Only a dead letter is run again; this run is {run.Status}.");
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(JobsAuditEvents.RunRerun, run.Job, JobsRecords.RunRecordType, run.Id, why, operatorName, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RunView.Of(run, false);
    }

    /// <summary>
    /// The dead letters of jobs and, when <paramref name="withEvents"/>, of event subscriptions, newest first (ADR-0061):
    /// one page of the two lists merged, each with what runs it again.
    /// </summary>
    public async Task<Page<DeadLetterView>> DeadLettersAsync(PageRequest page, bool withEvents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var want = page.PageNumber * page.PageSize;
        var jobsTotal = await context.Runs.CountAsync(r => r.Status == RunStates.Dead, cancellationToken).ConfigureAwait(false);
        var jobs = await context.Runs.AsNoTracking().Where(r => r.Status == RunStates.Dead)
            .OrderByDescending(r => r.DeadAt).ThenByDescending(r => r.Id).Take(want)
            .Select(r => new DeadLetterView("job", r.Id, r.Job, r.Key, r.Attempts, r.Error, r.DeadAt ?? r.UpdatedAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var events = new List<DeadLetterView>();
        var eventsTotal = 0;
        if (withEvents && eventDeadLetters is not null)
        {
            eventsTotal = await eventDeadLetters.CountAsync(cancellationToken).ConfigureAwait(false);
            events.AddRange((await eventDeadLetters.NewestAsync(0, want, cancellationToken).ConfigureAwait(false))
                .Select(e => new DeadLetterView("event", e.EventId, e.Subscriber, string.Create(CultureInfo.InvariantCulture, $"{e.Type} (version {e.Version})"), e.Attempts, e.LastError, e.DeadAt)));
        }

        var merged = jobs.Concat(events).OrderByDescending(d => d.DeadAt).ThenBy(d => d.Kind, StringComparer.Ordinal)
            .Skip((page.PageNumber - 1) * page.PageSize).Take(page.PageSize).ToList();
        return new Page<DeadLetterView>(merged, page, jobsTotal + eventsTotal);
    }

    private async Task<Result<JobView>> ChangeAsync(string name, string? reason, string? operatorName, string action, Func<JobRecord, string, DateTimeOffset, Result> change, CancellationToken cancellationToken)
    {
        var why = reason?.Trim();
        var refused = Refusal(name, declarations.Find(name), why);
        if (refused is not null)
        {
            return refused;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var record = await context.Jobs.FromSql($"select * from kernel_jobs.jobs where name = {name} for update").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            // A job a new build brought, before the runner's first tick made its record.
            var job = declarations.Find(name)!;
            record = new JobRecord(name, job.Suite, job.FirstDue(time.GetUtcNow(), TimeZoneInfo.Utc));
            context.Jobs.Add(record);
        }

        var changed = change(record, why!, time.GetUtcNow());
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(action, name, JobsRecords.JobRecordType, record.Id, why!, operatorName, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(name, cancellationToken).ConfigureAwait(false);
    }

    private static Error? Refusal(string name, DeclaredJob? job, string? why)
    {
        if (job is null)
        {
            return Error.NotFound(JobsErrors.JobNotFound, $"There is no job '{name}' in this unit.");
        }

        if (job.Definition.Scope == JobScope.EachProcess)
        {
            return Error.Conflict(JobsErrors.JobEachProcess, $"The job '{name}' runs in each process for what that process keeps in memory; it cannot be paused or started from here.");
        }

        return string.IsNullOrEmpty(why) ? ReasonRequired() : null;
    }

    private static Error ReasonRequired() =>
        Error.Validation(JobsErrors.ReasonRequired, "Say why: the audit trail keeps it with your name.", new FieldError("reason", "required", "Say why."));

    private Task WriteAsync(string action, string job, string recordType, Guid recordId, string reason, string? operatorName, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["job"] = job };
        if (operatorName is not null)
        {
            details["operator"] = operatorName;
            details["source"] = "control_plane";
        }

        return audit.WriteAsync(new AuditEvent(action, AuditOutcome.Success, actor.Id, job, details, recordType, recordId, reason, Severity: AuditSeverity.High), cancellationToken);
    }
}
