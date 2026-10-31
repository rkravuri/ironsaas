using System.Globalization;
using System.Text.Json;
using Iron.Kernel.Samples.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Samples.Services;

/// <summary>
/// <c>samples.tally_records</c> (ADR-0057, ADR-0061): counts the unit's sample records into its run's log. It runs only
/// when started, so the smoke can start it through the job console and find its run; a repeated run counts again and
/// changes nothing.
/// </summary>
public sealed class TallyRecordsJob(SamplesDbContext context) : IJob
{
    public static readonly JobDefinition Definition = JobDefinition.Create<TallyRecordsJob>(
        "samples.tally_records", "Counts the unit's sample records into the run's log, for the smoke.", "Platform engineering",
        JobSchedule.Explicit, PriorityClass.Interactive, TimeSpan.FromMinutes(1));

    public async Task<Result> RunAsync(JobContext run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var count = await context.Records.CountAsync(cancellationToken).ConfigureAwait(false);
        run.Log(string.Create(CultureInfo.InvariantCulture, $"The unit has {count} sample record(s)."));
        return Result.Success();
    }
}

/// <summary>
/// <c>samples.saturate</c> (TF-122 AC1, ADR-0061): the fairness proof's backlog. Started without a payload, a run queues
/// <see cref="DefaultBacklog"/> runs of the job (or <c>backlog</c>), each of which holds a database connection and keeps
/// one processor busy for <c>seconds</c>, eight at once, until <c>minutes</c> from the start; a run queued past that time
/// ends at once, so the backlog drains itself. Pausing the job stops it sooner. It is bulk work, so the unit's own
/// interactive and normal jobs still go first; another tenant's unit is held apart by its limits.
/// </summary>
public sealed class SaturateJob(SamplesDbContext context, IJobStarter starter, TimeProvider time) : IJob
{
    public const int DefaultBacklog = 10_000;

    public static readonly JobDefinition Definition = JobDefinition.Create<SaturateJob>(
        "samples.saturate", "Queues a backlog of bulk runs that hold the unit's connections and processors, for the fairness proof.", "Platform engineering",
        JobSchedule.Explicit, PriorityClass.Bulk, TimeSpan.FromMinutes(10), new RetryPolicy(1, TimeSpan.Zero, TimeSpan.Zero), maxParallel: 8);

    public async Task<Result> RunAsync(JobContext run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var asked = Asked.From(run.Payload);
        if (asked.Until is not { } until)
        {
            return await QueueAsync(run, asked, cancellationToken).ConfigureAwait(false);
        }

        if (time.GetUtcNow() >= until)
        {
            run.Log("Past the backlog's time: nothing to do.");
            return Result.Success();
        }

        // One connection held and one processor kept busy for the run's seconds, as an export would.
        var seconds = asked.Seconds;
        var sleeping = context.Database.ExecuteSqlAsync($"select pg_sleep({seconds})", cancellationToken);
        var started = time.GetTimestamp();
        await Task.Run(
            () =>
            {
                while (time.GetElapsedTime(started).TotalSeconds < seconds && !cancellationToken.IsCancellationRequested)
                {
                    Thread.SpinWait(100_000);
                }
            },
            cancellationToken).ConfigureAwait(false);
        await sleeping.ConfigureAwait(false);
        run.Log(string.Create(CultureInfo.InvariantCulture, $"Held a connection and a processor for {seconds} s."));
        return Result.Success();
    }

    private async Task<Result> QueueAsync(JobContext run, Asked asked, CancellationToken cancellationToken)
    {
        var until = time.GetUtcNow().AddMinutes(asked.Minutes);
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal) { ["until"] = until, ["seconds"] = asked.Seconds });
        for (var i = 1; i <= asked.Backlog; i++)
        {
            // Keyed by this run, so a retried queuing adds no second backlog (TF-120 AC3).
            await starter.StartAsync(Definition, string.Create(CultureInfo.InvariantCulture, $"{run.RunId:N}-{i}"), payload, cancellationToken).ConfigureAwait(false);
        }

        run.Log(string.Create(CultureInfo.InvariantCulture, $"Queued {asked.Backlog} bulk run(s) of {asked.Seconds} s each, until {until:O}."));
        return Result.Success();
    }

    /// <summary>What a run was given: a queuing run's backlog and minutes, and every run's seconds and end.</summary>
    private sealed record Asked(int Backlog, int Seconds, int Minutes, DateTimeOffset? Until)
    {
        public static Asked From(string? payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return new Asked(DefaultBacklog, 15, 5, null);
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return new Asked(
                Math.Clamp(Number(root, "backlog") ?? DefaultBacklog, 1, 100_000),
                Math.Clamp(Number(root, "seconds") ?? 15, 1, 300),
                Math.Clamp(Number(root, "minutes") ?? 5, 1, 60),
                root.TryGetProperty("until", out var until) && until.ValueKind == JsonValueKind.String ? until.GetDateTimeOffset() : null);
        }

        private static int? Number(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
    }
}
