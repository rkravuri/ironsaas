using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Iron.Kernel;

/// <summary>
/// The order a unit takes its work in (TF-122 AC2, ADR-0061): what a person waits for first, then the platform's
/// housekeeping, then bulk work. A job declares one; an undefined class is refused when the job is declared (TF-122 AC4).
/// </summary>
public enum PriorityClass
{
    Interactive = 0,
    Normal = 1,
    Bulk = 2,
}

/// <summary>
/// Where a job runs (ADR-0061): once per unit per interval, coordinated through the unit's database however many worker
/// processes run (the tenant's unit, or the control plane), or in every process of the unit, for what each process keeps
/// in its own memory, such as the cached flags.
/// </summary>
public enum JobScope
{
    Unit,
    EachProcess,
}

public enum JobScheduleKind
{
    Interval,
    Cron,
    Explicit,
}

/// <summary>
/// When a job runs (TF-120): every interval, at the times of a cron expression in the tenant's time zone, or only when
/// code or a person starts it (an explicit start, which a subscription to a domain event makes an event trigger). An
/// interval may be read from the configuration key a module already had, so its existing settings keep working.
/// </summary>
public sealed record JobSchedule
{
    private JobSchedule(JobScheduleKind kind, TimeSpan? every, string? cron, TimeSpan firstAfter, string? configurationKey)
    {
        Kind = kind;
        Every = every;
        Cron = cron;
        FirstAfter = firstAfter;
        ConfigurationKey = configurationKey;
    }

    public JobScheduleKind Kind { get; }

    public TimeSpan? Every { get; }

    public string? Cron { get; }

    /// <summary>How long after a unit first schedules the job its first interval ends: a daily job need not run at every start.</summary>
    public TimeSpan FirstAfter { get; }

    /// <summary>A configuration key, in seconds, that replaces <see cref="Every"/> in a unit that sets it.</summary>
    public string? ConfigurationKey { get; }

    public static JobSchedule Explicit { get; } = new(JobScheduleKind.Explicit, null, null, TimeSpan.Zero, null);

    public static JobSchedule Interval(TimeSpan every, TimeSpan? firstAfter = null, string? configurationKey = null)
    {
        if (every <= TimeSpan.Zero || every > TimeSpan.FromDays(31))
        {
            throw new ArgumentOutOfRangeException(nameof(every), every, "A job's interval is more than nothing and at most 31 days.");
        }

        return new JobSchedule(JobScheduleKind.Interval, every, null, firstAfter ?? TimeSpan.Zero, configurationKey);
    }

    /// <summary>A cron expression of five fields (minute, hour, day of month, month, day of week), in the tenant's time zone.</summary>
    public static JobSchedule CronExpression(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return new JobSchedule(JobScheduleKind.Cron, null, expression.Trim(), TimeSpan.Zero, null);
    }

    /// <summary>The schedule in words for the catalogue: <c>every 5s</c>, <c>cron 0 2 * * *</c>, or <c>when started</c>.</summary>
    public string Describe() => Kind switch
    {
        JobScheduleKind.Interval => $"every {Words(Every!.Value)}",
        JobScheduleKind.Cron => $"cron {Cron}",
        _ => "when started",
    };

    public static string Words(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } when span.TotalDays % 1 == 0 => $"{span.TotalDays:0}d",
        { TotalHours: >= 1 } when span.TotalHours % 1 == 0 => $"{span.TotalHours:0}h",
        { TotalMinutes: >= 1 } when span.TotalMinutes % 1 == 0 => $"{span.TotalMinutes:0}m",
        { TotalSeconds: >= 1 } when span.TotalSeconds % 1 == 0 => $"{span.TotalSeconds:0}s",
        _ => $"{span.TotalMilliseconds:0}ms",
    };
}

/// <summary>
/// How a failed run is tried again (TF-123): the attempts in all, and a wait that doubles from the first delay up to the
/// longest, moved by up to a fifth either way at random so that many runs failing together do not retry together. After
/// the last attempt the run is a dead letter, with its error, and an alert.
/// </summary>
public sealed record RetryPolicy
{
    public RetryPolicy(int attempts, TimeSpan firstDelay, TimeSpan longestDelay)
    {
        if (attempts is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(attempts), attempts, "A job is attempted from 1 to 20 times.");
        }

        if (firstDelay < TimeSpan.Zero || longestDelay < firstDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(longestDelay), longestDelay, "The longest delay is at least the first, and neither is negative.");
        }

        Attempts = attempts;
        FirstDelay = firstDelay;
        LongestDelay = longestDelay;
    }

    /// <summary>Five attempts, waiting about 10 seconds, then 20, 40 and 80.</summary>
    public static RetryPolicy Default { get; } = new(5, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10));

    public int Attempts { get; }

    public TimeSpan FirstDelay { get; }

    public TimeSpan LongestDelay { get; }

    /// <summary>The share of the delay that jitter moves it by, either way.</summary>
    public const double JitterShare = 0.2;

    /// <summary>A jitter from -1 to 1 for <see cref="DelayAfter"/>, drawn from the system's generator.</summary>
    public static double Jitter() => RandomNumberGenerator.GetInt32(-1000, 1001) / 1000.0;

    /// <summary>
    /// The wait after <paramref name="failures"/> failed attempts: the first delay doubled for each failure after the first,
    /// at most the longest, then moved by <paramref name="jitter"/> (from -1 to 1) times a fifth of itself.
    /// </summary>
    public TimeSpan DelayAfter(int failures, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failures, 1);
        var doubled = FirstDelay.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 30));
        var capped = Math.Min(doubled, LongestDelay.TotalMilliseconds);
        var moved = capped * (1 + (Math.Clamp(jitter, -1, 1) * JitterShare));
        return TimeSpan.FromMilliseconds(Math.Max(0, moved));
    }
}

/// <summary>
/// A job a module declares (TF-120, ADR-0061) and lists in its <c>IModule.Jobs</c>: its name, <c>module.what_it_does</c>,
/// what it does and who owns it, when it runs, where, how long a run may take, how a failure is retried, how many runs
/// may run at once, its priority class, and what makes a run unique, since a job must be idempotent: a run repeated by a
/// retry, a re-run or a second worker has the effect of one. Its handler is resolved from the run's own scope.
/// </summary>
public sealed partial class JobDefinition
{
    public const int MaxNameLength = 100;

    private JobDefinition(string name, string description, string owner, JobSchedule schedule, PriorityClass priority, TimeSpan timeout, RetryPolicy retry, JobScope scope, int maxParallel, string uniqueBy, Type handlerType)
    {
        Name = name;
        Module = name[..name.IndexOf('.', StringComparison.Ordinal)];
        Description = description;
        Owner = owner;
        Schedule = schedule;
        Priority = priority;
        Timeout = timeout;
        Retry = retry;
        Scope = scope;
        MaxParallel = maxParallel;
        UniqueBy = uniqueBy;
        HandlerType = handlerType;
    }

    public string Name { get; }

    /// <summary>The module that declares the job: its name's first part.</summary>
    public string Module { get; }

    public string Description { get; }

    public string Owner { get; }

    public JobSchedule Schedule { get; }

    public PriorityClass Priority { get; }

    public TimeSpan Timeout { get; }

    public RetryPolicy Retry { get; }

    public JobScope Scope { get; }

    /// <summary>How many runs of the job may run at once in the unit: 1 is one at a time.</summary>
    public int MaxParallel { get; }

    /// <summary>What makes a run unique, in words: a second run with the same key is the same run.</summary>
    public string UniqueBy { get; }

    public Type HandlerType { get; }

    public static JobDefinition Create<THandler>(
        string name,
        string description,
        string owner,
        JobSchedule schedule,
        PriorityClass priority,
        TimeSpan timeout,
        RetryPolicy? retry = null,
        JobScope scope = JobScope.Unit,
        int maxParallel = 1,
        string? uniqueBy = null)
        where THandler : IJob
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (name is null || name.Length > MaxNameLength || !NamePattern().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a job's name: module.what_it_does, in lowercase with underscores (ADR-0061).", nameof(name));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (!Enum.IsDefined(priority))
        {
            // TF-122 AC4: work of a class nobody defined is refused, here, where the job is declared.
            throw new ArgumentOutOfRangeException(nameof(priority), priority, $"The job '{name}' names a priority class that is not defined: use Interactive, Normal or Bulk (ADR-0061).");
        }

        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, $"The job '{name}' names a scope that is not defined.");
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, $"The job '{name}' has a timeout of more than nothing and at most 24 hours.");
        }

        if (maxParallel is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(maxParallel), maxParallel, $"The job '{name}' runs from 1 to 16 runs at once.");
        }

        if (scope == JobScope.EachProcess && (schedule.Kind != JobScheduleKind.Interval || maxParallel != 1))
        {
            throw new ArgumentException($"The job '{name}' runs in each process, which needs an interval and one run at a time.", nameof(scope));
        }

        var unique = uniqueBy ?? (schedule.Kind == JobScheduleKind.Explicit ? "the key its starter gives" : "the time it was scheduled for");
        return new JobDefinition(name, description.Trim(), owner.Trim(), schedule, priority, timeout, retry ?? RetryPolicy.Default, scope, maxParallel, unique, typeof(THandler));
    }

    public override string ToString() => Name;

    [GeneratedRegex("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

/// <summary>A job's work (TF-120): one run, which must be idempotent, and answers a failure rather than throwing for an expected one.</summary>
public interface IJob
{
    Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken);
}

/// <summary>
/// One run as its handler sees it: which run, its key and attempt, the time it was scheduled for, what its starter gave
/// it, and a log the console shows with the run (TF-121 AC1). The log keeps its last 8000 characters.
/// </summary>
public sealed class JobContext(Guid runId, string job, string key, int attempt, DateTimeOffset scheduledFor, string? payload, Guid? startedBy)
{
    public const int LogMaxLength = 8000;

    private readonly StringBuilder _log = new();
    private readonly Lock _gate = new();

    public Guid RunId { get; } = runId;

    public string Job { get; } = job;

    public string Key { get; } = key;

    /// <summary>The attempt this is, from 1.</summary>
    public int Attempt { get; } = attempt;

    public DateTimeOffset ScheduledFor { get; } = scheduledFor;

    public string? Payload { get; } = payload;

    /// <summary>The person or client who started it; null for a scheduled run.</summary>
    public Guid? StartedBy { get; } = startedBy;

    /// <summary>A line for the run's log: what it did, never a secret or a person's data beyond an identifier.</summary>
    public void Log(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            _log.Append(line.Trim()).Append('\n');
            if (_log.Length > LogMaxLength)
            {
                _log.Remove(0, _log.Length - LogMaxLength);
            }
        }
    }

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _log.ToString();
            }
        }
    }
}

/// <summary>
/// Starts a run of a job now (TF-120, ADR-0061): an explicit job, or a scheduled one out of its schedule, keyed so a
/// second start with the same key is the same run. Inside a unit of work the run commits with the change that asked for
/// it, so a subscription to a domain event starts a job exactly once per event. A run started in a request keeps the
/// request's trace, and its span links to it (TF-130 AC3).
/// </summary>
public interface IJobStarter
{
    Task<Guid> StartAsync(JobDefinition job, string key, string? payload, CancellationToken cancellationToken);
}
