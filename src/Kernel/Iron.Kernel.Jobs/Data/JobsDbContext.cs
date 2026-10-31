using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Jobs.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Jobs.Data;

/// <summary>
/// The unit's state of one job it runs (TF-120, TF-121, ADR-0061): whether it is paused, by whom and why, and for a
/// scheduled job the next time it falls due, which the scheduler moves on in the same statement that queues the run,
/// so two workers never queue one occurrence twice.
/// </summary>
public sealed class JobRecord : AuditedEntity, ITenantWide
{
    public const int NameMaxLength = JobDefinition.MaxNameLength;
    public const int ReasonMaxLength = 500;

    private JobRecord()
    {
        Name = string.Empty;
    }

    public JobRecord(string name, string? suite, DateTimeOffset? nextDueAt)
        : this()
    {
        Name = name;
        Suite = suite;
        NextDueAt = nextDueAt;
    }

    public string Name { get; private set; }

    /// <summary>The suite of the declaring module, or null for the Foundation's: a removed suite's job is held.</summary>
    public string? Suite { get; private set; }

    public bool Paused { get; private set; }

    public DateTimeOffset? PausedAt { get; private set; }

    public Guid? PausedBy { get; private set; }

    public string? PauseReason { get; private set; }

    /// <summary>When the scheduled job next falls due; null for an explicit one.</summary>
    public DateTimeOffset? NextDueAt { get; private set; }

    /// <summary>The occurrence the scheduler last queued.</summary>
    public DateTimeOffset? LastScheduledFor { get; private set; }

    public bool Pause(Guid by, string reason, DateTimeOffset at)
    {
        if (Paused)
        {
            return false;
        }

        Paused = true;
        PausedAt = at;
        PausedBy = by;
        PauseReason = reason;
        return true;
    }

    /// <summary>Resumed: a scheduled job falls due next at <paramref name="nextDueAt"/>, so the time it was paused is not missed.</summary>
    public bool Resume(DateTimeOffset? nextDueAt)
    {
        if (!Paused)
        {
            return false;
        }

        Paused = false;
        PausedAt = null;
        PausedBy = null;
        PauseReason = null;
        NextDueAt = nextDueAt;
        return true;
    }

    /// <summary>The scheduler queued <paramref name="occurrence"/>: the job falls due next at <paramref name="next"/>.</summary>
    public void Advance(DateTimeOffset? next, DateTimeOffset occurrence)
    {
        NextDueAt = next;
        LastScheduledFor = occurrence;
    }

    /// <summary>A job a newer build schedules differently, or one that turns from explicit to scheduled, starts over from now.</summary>
    public void Reschedule(DateTimeOffset? nextDueAt) => NextDueAt = nextDueAt;
}

/// <summary>
/// One run of a job (TF-120, TF-121, TF-123, ADR-0061). Unique by its job and key, the scheduled time or the start's key,
/// so a second worker, a retry or a second start never makes a second run. Queued until its time and a free slot, taken
/// by one worker under a lease, then succeeded; a failure waits in the queue for its retry, and the last makes it a dead
/// letter, kept until someone runs it again. A run that started in a request keeps that request's trace (TF-130 AC3).
/// </summary>
public sealed class JobRun : AuditedEntity, ITenantWide
{
    public const int KeyMaxLength = 200;
    public const int HolderMaxLength = 100;
    public const int ErrorMaxLength = 1000;
    public const int ReasonMaxLength = JobRecord.ReasonMaxLength;
    public const int PayloadMaxLength = 1000;
    public const int TraceMaxLength = 64;

    private JobRun()
    {
        Job = string.Empty;
        Key = string.Empty;
        Status = RunStates.Queued;
        Log = string.Empty;
    }

    public JobRun(string job, string key, string? suite, PriorityClass priority, DateTimeOffset scheduledFor, DateTimeOffset dueAt, Guid? triggeredBy = null, string? reason = null, string? payload = null, string? traceParent = null)
        : this()
    {
        Job = job;
        Key = key.Length <= KeyMaxLength ? key : throw new ArgumentException($"A run's key is at most {KeyMaxLength} characters.", nameof(key));
        Suite = suite;
        Priority = (int)priority;
        ScheduledFor = scheduledFor;
        DueAt = dueAt;
        TriggeredBy = triggeredBy;
        Reason = reason;
        Payload = payload is null || payload.Length <= PayloadMaxLength ? payload : throw new ArgumentException($"A run's payload is at most {PayloadMaxLength} characters.", nameof(payload));
        TraceParent = traceParent is { Length: <= TraceMaxLength } ? traceParent : null;
    }

    public string Job { get; private set; }

    public string Key { get; private set; }

    public string? Suite { get; private set; }

    public string Status { get; private set; }

    /// <summary>The job's priority class when the run was queued, as its number: interactive 0, normal 1, bulk 2.</summary>
    public int Priority { get; private set; }

    /// <summary>The attempts made so far.</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset ScheduledFor { get; private set; }

    /// <summary>When it may next be taken: its scheduled time, or its retry's.</summary>
    public DateTimeOffset DueAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public int? DurationMs { get; private set; }

    /// <summary>The worker process that holds it while it runs.</summary>
    public string? Holder { get; private set; }

    /// <summary>Until when the holder's claim stands: past it, the run is the next worker's (a crashed holder's run is recovered).</summary>
    public DateTimeOffset? LeaseUntil { get; private set; }

    public string? Error { get; private set; }

    /// <summary>What the run wrote to its log, its last attempt's (TF-121 AC1).</summary>
    public string Log { get; private set; }

    public Guid? TriggeredBy { get; private set; }

    public string? Reason { get; private set; }

    public string? Payload { get; private set; }

    /// <summary>The W3C trace context of the request that started it, which the run's span links to (TF-130 AC3).</summary>
    public string? TraceParent { get; private set; }

    public DateTimeOffset? DeadAt { get; private set; }

    public DateTimeOffset? HeldAt { get; private set; }

    /// <summary>Times someone ran it again after it failed.</summary>
    public int Reruns { get; private set; }

    public void Claim(string holder, DateTimeOffset at, DateTimeOffset leaseUntil)
    {
        Status = RunStates.Running;
        Attempts++;
        Holder = holder.Length <= HolderMaxLength ? holder : holder[..HolderMaxLength];
        StartedAt = at;
        FinishedAt = null;
        DurationMs = null;
        LeaseUntil = leaseUntil;
    }

    public void Succeeded(DateTimeOffset at, string log)
    {
        Status = RunStates.Succeeded;
        Finish(at, log);
        Error = null;
    }

    /// <summary>A failed attempt: queued for its retry at <paramref name="retryAt"/>, or, after the last, a dead letter.</summary>
    public void Failed(DateTimeOffset at, string error, string log, DateTimeOffset? retryAt)
    {
        Finish(at, log);
        Error = error.Length <= ErrorMaxLength ? error : error[..(ErrorMaxLength - 1)] + "…";
        if (retryAt is { } next)
        {
            Status = RunStates.Queued;
            DueAt = next;
            return;
        }

        Status = RunStates.Dead;
        DeadAt = at;
    }

    /// <summary>Its time came while the tenant was suspended (TF-122 AC3): not run, with why in its log.</summary>
    public void Skip(DateTimeOffset at, string why)
    {
        Status = RunStates.Skipped;
        FinishedAt = at;
        Log = why;
    }

    /// <summary>A dead letter run again, by a person or the operator: queued now, its attempts from nothing, its last error kept until it succeeds.</summary>
    public bool Rerun(DateTimeOffset at)
    {
        if (Status != RunStates.Dead)
        {
            return false;
        }

        Status = RunStates.Queued;
        Attempts = 0;
        DueAt = at;
        DeadAt = null;
        Reruns++;
        return true;
    }

    public bool Hold(DateTimeOffset at)
    {
        if (Status != RunStates.Queued)
        {
            return false;
        }

        Status = RunStates.Held;
        HeldAt = at;
        return true;
    }

    public bool Resume(DateTimeOffset at)
    {
        if (Status != RunStates.Held)
        {
            return false;
        }

        Status = RunStates.Queued;
        HeldAt = null;
        DueAt = at;
        return true;
    }

    private void Finish(DateTimeOffset at, string log)
    {
        FinishedAt = at;
        DurationMs = StartedAt is { } started ? (int)Math.Clamp((at - started).TotalMilliseconds, 0, int.MaxValue) : null;
        Holder = null;
        LeaseUntil = null;
        Log = log.Length <= JobContext.LogMaxLength ? log : log[^JobContext.LogMaxLength..];
    }
}

[Schema("kernel_jobs")]
public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options) : ModuleDbContext(options)
{
    public DbSet<JobRecord> Jobs => Set<JobRecord>();

    public DbSet<JobRun> Runs => Set<JobRun>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<JobRecord>(job =>
        {
            job.ToTable("jobs");
            job.Property(j => j.Name).HasMaxLength(JobRecord.NameMaxLength);
            job.Property(j => j.Suite).HasMaxLength(40);
            job.Property(j => j.PauseReason).HasMaxLength(JobRecord.ReasonMaxLength);
            job.HasIndex(j => j.Name).IsUnique();
        });

        modelBuilder.Entity<JobRun>(run =>
        {
            run.ToTable("runs");
            run.Property(r => r.Job).HasMaxLength(JobRecord.NameMaxLength);
            run.Property(r => r.Key).HasMaxLength(JobRun.KeyMaxLength);
            run.Property(r => r.Suite).HasMaxLength(40);
            run.Property(r => r.Status).HasMaxLength(10);
            run.Property(r => r.Holder).HasMaxLength(JobRun.HolderMaxLength);
            run.Property(r => r.Error).HasMaxLength(JobRun.ErrorMaxLength);
            run.Property(r => r.Log).HasMaxLength(JobContext.LogMaxLength);
            run.Property(r => r.Reason).HasMaxLength(JobRun.ReasonMaxLength);
            run.Property(r => r.Payload).HasMaxLength(JobRun.PayloadMaxLength);
            run.Property(r => r.TraceParent).HasMaxLength(JobRun.TraceMaxLength);

            // One run per job and key: the scheduled time, or the start's key (TF-120 AC1).
            run.HasIndex(r => new { r.Job, r.Key }).IsUnique();

            // What may be taken, in priority order (TF-122 AC2), and what a crashed holder left.
            run.HasIndex(r => new { r.Priority, r.DueAt }, "ix_runs_queued").HasFilter("status = 'queued'").HasDatabaseName("ix_runs_queued");
            run.HasIndex(r => r.LeaseUntil, "ix_runs_running").HasFilter("status = 'running'").HasDatabaseName("ix_runs_running");
            run.HasIndex(r => new { r.Job, r.StartedAt });
            run.HasIndex(r => new { r.Status, r.DeadAt });
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class JobsDbContextFactory : IDesignTimeDbContextFactory<JobsDbContext>
{
    public JobsDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<JobsDbContext>());
}
