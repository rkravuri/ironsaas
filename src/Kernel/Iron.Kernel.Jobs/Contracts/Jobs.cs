using Iron.Kernel.History;

namespace Iron.Kernel.Jobs.Contracts;

/// <summary>The Jobs module's permissions (TF-121, ADR-0061).</summary>
public static class JobsPermissions
{
    public static readonly Permission JobsRead = new("jobs.jobs.read", "See the jobs, their runs, logs and backlog, and the dead letters of jobs and events.", Risk.Medium);

    /// <summary>Pausing a maintenance job such as the audit chain's sealing stops what the platform relies on: high, with a recent sign-in.</summary>
    public static readonly Permission JobsManage = new("jobs.jobs.manage", "Pause and resume jobs, run them now, and run a failed run again.", Risk.High);

    public static IReadOnlyList<Permission> All => [JobsRead, JobsManage];
}

/// <summary>The Jobs module's audit events (ADR-0061): what people did with jobs, and the failures that raise alerts.</summary>
public static class JobsAuditEvents
{
    public const string JobPaused = "jobs.job_paused";
    public const string JobResumed = "jobs.job_resumed";

    /// <summary>A person, the operator or code started a run now, with a reason.</summary>
    public const string RunTriggered = "jobs.run_triggered";

    /// <summary>A failed run was run again, with a reason.</summary>
    public const string RunRerun = "jobs.run_rerun";

    /// <summary>A run failed as many times as its job is retried: a dead letter, which raises an alert (TF-123 AC2).</summary>
    public const string RunDeadLettered = "jobs.run_dead_lettered";

    /// <summary>A scheduled job ran more than one interval late, so occurrences were passed over (TF-123 AC3).</summary>
    public const string ScheduleMissed = "jobs.schedule_missed";

    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        new AuditEventType(JobPaused, "Job paused").AsAdministrative(),
        new AuditEventType(JobResumed, "Job resumed").AsAdministrative(),
        new AuditEventType(RunTriggered, "Job run started now").AsAdministrative(),
        new AuditEventType(RunRerun, "Failed job run run again").AsAdministrative(),
        new(RunDeadLettered, "Job run parked as a dead letter"),
        new(ScheduleMissed, "Job missed its schedule"),
    ];
}

public static class JobsErrors
{
    public const string JobNotFound = "jobs.job.not_found";
    public const string RunNotFound = "jobs.run.not_found";

    /// <summary>A job every process runs for itself has no state to pause and no run to start (ADR-0061).</summary>
    public const string JobEachProcess = "jobs.job.each_process";

    /// <summary>The job belongs to a suite this unit no longer runs: its runs are held until the suite returns.</summary>
    public const string JobHeld = "jobs.job.held";

    public const string AlreadyPaused = "jobs.job.already_paused";
    public const string NotPaused = "jobs.job.not_paused";
    public const string ReasonRequired = "jobs.reason_required";

    /// <summary>Only a failed run, one that is a dead letter or failed its last attempt, is run again.</summary>
    public const string RunNotFailed = "jobs.run.not_failed";
}

/// <summary>The Jobs module's settings (ADR-0036).</summary>
public static class JobsSettings
{
    /// <summary>The time zone a cron schedule is read in (TF-120): the tenant's, since a "2 a.m." job means its night.</summary>
    public static readonly Setting<string> TimeZone = Setting.Text(
        "jobs.time_zone",
        "The time zone the jobs' cron schedules are read in, such as Europe/London; UTC unless set.",
        "UTC",
        SettingScopes.Tenant,
        SettingRule.Matches("^[A-Za-z]+(/[A-Za-z0-9_+-]+)*$", "a time zone's name, such as Europe/London or UTC"));

    /// <summary>How long finished runs are kept to look back at; dead letters stay until they are run again.</summary>
    public static readonly Setting<int> HistoryDays = Setting.WholeNumber(
        "jobs.history.days", "How many days a finished job run stays to look back at; a dead letter stays until it is run again.", 14, 1, 365, SettingScopes.Tenant);

    public static IReadOnlyList<SettingDefinition> All => [TimeZone, HistoryDays];
}

/// <summary>Where a run stands (ADR-0061).</summary>
public static class RunStates
{
    /// <summary>Waiting for its time or a free slot; a failed attempt waits here for its retry.</summary>
    public const string Queued = "queued";

    public const string Running = "running";
    public const string Succeeded = "succeeded";

    /// <summary>Failed its last attempt: a dead letter, alerted, kept until it is run again.</summary>
    public const string Dead = "dead";

    /// <summary>Its time came while the tenant was suspended, so it did not run (TF-122 AC3).</summary>
    public const string Skipped = "skipped";

    /// <summary>Of a job of a suite the unit no longer runs: kept, neither run nor alerted, until the suite returns.</summary>
    public const string Held = "held";
}
