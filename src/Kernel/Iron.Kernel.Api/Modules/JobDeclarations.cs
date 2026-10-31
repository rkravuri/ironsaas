using System.Globalization;
using Cronos;
using Microsoft.Extensions.Configuration;

namespace Iron.Kernel.Api.Modules;

/// <summary>
/// The jobs a host's modules declare (TF-120, ADR-0061), registered by AddModules, each with the interval the unit's
/// configuration gives it. A unit without a suite declares none of the suite's jobs, since it runs none of its modules
/// (TF-244): the runner holds the runs it finds of such a job until the suite returns.
/// </summary>
public sealed class JobDeclarations
{
    private readonly Dictionary<string, DeclaredJob> _jobs = new(StringComparer.Ordinal);

    public JobDeclarations(IEnumerable<IModule> modules, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        foreach (var module in modules)
        {
            foreach (var job in module.Jobs)
            {
                if (!string.Equals(job.Module, module.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"The module '{module.Name}' declares the job '{job.Name}', but a job's name starts with the name of the module that declares it.");
                }

                var every = job.Schedule.Every;
                if (job.Schedule.ConfigurationKey is { } key && configuration?[key] is { Length: > 0 } configured)
                {
                    every = double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                        ? TimeSpan.FromSeconds(seconds)
                        : throw new InvalidOperationException($"{key} is '{configured}', not a number of seconds above 0, for the job '{job.Name}'.");
                }

                CronExpression? cron = null;
                if (job.Schedule.Kind == JobScheduleKind.Cron)
                {
                    try
                    {
                        cron = CronExpression.Parse(job.Schedule.Cron!);
                    }
                    catch (CronFormatException exception)
                    {
                        throw new InvalidOperationException($"The job '{job.Name}' has the cron expression '{job.Schedule.Cron}', which is not one of five fields: {exception.Message}", exception);
                    }
                }

                var suite = module.Suite == SuiteName.Foundation ? null : module.Suite;
                if (!_jobs.TryAdd(job.Name, new DeclaredJob(job, suite, every, cron)))
                {
                    throw new InvalidOperationException($"The job '{job.Name}' is declared twice.");
                }
            }
        }
    }

    /// <summary>Every job, by name.</summary>
    public IReadOnlyList<DeclaredJob> Jobs => [.. _jobs.Values.OrderBy(j => j.Name, StringComparer.Ordinal)];

    /// <summary>The jobs that run once per unit per interval, through the unit's database.</summary>
    public IReadOnlyList<DeclaredJob> UnitJobs => [.. Jobs.Where(j => j.Definition.Scope == JobScope.Unit)];

    /// <summary>The jobs every process runs for itself.</summary>
    public IReadOnlyList<DeclaredJob> ProcessJobs => [.. Jobs.Where(j => j.Definition.Scope == JobScope.EachProcess)];

    public DeclaredJob? Find(string name) => _jobs.GetValueOrDefault(name);
}

/// <summary>A declared job as a host runs it: its declaration, its suite (null for the Foundation's), and its effective schedule.</summary>
public sealed class DeclaredJob(JobDefinition definition, string? suite, TimeSpan? every, CronExpression? cron)
{
    /// <summary>The most occurrences a catch-up counts before it stops counting: a schedule missed for weeks is missed.</summary>
    public const int MissedCountLimit = 1000;

    public JobDefinition Definition { get; } = definition;

    public string Name => Definition.Name;

    public string? Suite { get; } = suite;

    /// <summary>The interval this unit runs it at: the declaration's, or the configuration's where it gives one.</summary>
    public TimeSpan? Every { get; } = every;

    public bool IsScheduled => Definition.Schedule.Kind != JobScheduleKind.Explicit;

    /// <summary>The schedule in words, with the interval this unit uses.</summary>
    public string Describe() => Definition.Schedule.Kind == JobScheduleKind.Interval ? $"every {JobSchedule.Words(Every!.Value)}" : Definition.Schedule.Describe();

    /// <summary>The first occurrence when the unit first schedules the job at <paramref name="now"/>.</summary>
    public DateTimeOffset? FirstDue(DateTimeOffset now, TimeZoneInfo zone) => Definition.Schedule.Kind switch
    {
        JobScheduleKind.Interval => now + Definition.Schedule.FirstAfter,
        JobScheduleKind.Cron => cron!.GetNextOccurrence(now, zone),
        _ => null,
    };

    /// <summary>The occurrence after <paramref name="occurrence"/>: an interval on from it, or the cron's next time in <paramref name="zone"/>.</summary>
    public DateTimeOffset? NextAfter(DateTimeOffset occurrence, TimeZoneInfo zone) => Definition.Schedule.Kind switch
    {
        JobScheduleKind.Interval => occurrence + Every!.Value,
        JobScheduleKind.Cron => cron!.GetNextOccurrence(occurrence, zone),
        _ => null,
    };

    /// <summary>
    /// The occurrence a late scheduler runs, and how many it passed over (TF-123 AC3): from <paramref name="due"/>, every
    /// occurrence up to <paramref name="now"/>; the run takes the latest, and the earlier ones are missed.
    /// </summary>
    public (DateTimeOffset Latest, int Missed) Catch(DateTimeOffset due, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (due > now || !IsScheduled)
        {
            return (due, 0);
        }

        if (Definition.Schedule.Kind == JobScheduleKind.Interval)
        {
            var every = Every!.Value;
            var steps = (long)Math.Floor((now - due).Ticks / (double)every.Ticks);
            return (due + TimeSpan.FromTicks(every.Ticks * steps), (int)Math.Min(steps, MissedCountLimit));
        }

        var latest = due;
        var missed = 0;
        foreach (var occurrence in cron!.GetOccurrences(due, now, zone, fromInclusive: false, toInclusive: true))
        {
            latest = occurrence;
            if (++missed >= MissedCountLimit)
            {
                break;
            }
        }

        return (latest, missed);
    }
}
