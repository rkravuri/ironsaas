using System.Globalization;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Seeding;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Jobs.Contracts;
using Iron.Kernel.Jobs.Data;
using Iron.Kernel.Settings.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Jobs.Services;

/// <summary>What the operator asked of the <c>jobs</c> command, which the fleet puts on the job as <c>Jobs__Command__*</c>.</summary>
public sealed class JobsCommandOptions
{
    public const string SectionName = "Jobs:Command";

    /// <summary><c>list</c>, <c>run</c>, <c>pause</c>, <c>resume</c> or <c>rerun</c>.</summary>
    public string? Action { get; set; }

    public string? Job { get; set; }

    /// <summary>The run a <c>rerun</c> runs again.</summary>
    public string? RunId { get; set; }

    /// <summary>Why, in the operator's words: every action but the list needs it.</summary>
    public string? Reason { get; set; }

    /// <summary>Who asked, as the control plane knows them: the tenant's audit trail names them.</summary>
    public string? Operator { get; set; }
}

/// <summary>
/// <c>jobs</c>: the operator's view and control of a tenant's jobs (TF-121, ADR-0061), which the fleet runs in the unit
/// when the operator asks the control plane. The list answers each job's state, schedule, next and last runs, backlog and
/// last failure; run, pause, resume and rerun act as an administrator's would, audited in the operator's name with the
/// reason. The job's output is the answer, and holds no run's log or payload.
/// </summary>
public sealed class JobsCommand(JobConsole console, JobsCommandOptions asked) : IUnitCommand
{
    public const string List = "list";
    public const string Run = "run";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Rerun = "rerun";

    public string Name => DatabaseCommands.Jobs;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var operatorName = asked.Operator ?? "the platform operator";
        switch (asked.Action)
        {
            case List:
                return await ListAsync(cancellationToken).ConfigureAwait(false);
            case Run:
                return await AnswerAsync(await console.TriggerAsync(asked.Job ?? string.Empty, asked.Reason, operatorName, cancellationToken).ConfigureAwait(false), run => $"Started a run of {run.Job} now: {run.Id:D}, queued for the unit's worker.").ConfigureAwait(false);
            case Pause:
                return await AnswerAsync(await console.PauseAsync(asked.Job ?? string.Empty, asked.Reason, operatorName, cancellationToken).ConfigureAwait(false), job => $"Paused {job.Name}: it queues no run until it is resumed.").ConfigureAwait(false);
            case Resume:
                return await AnswerAsync(await console.ResumeAsync(asked.Job ?? string.Empty, asked.Reason, operatorName, cancellationToken).ConfigureAwait(false), job => $"Resumed {job.Name}: next run {Time(job.NextRunAt)}.").ConfigureAwait(false);
            case Rerun:
                if (!Guid.TryParse(asked.RunId, out var runId))
                {
                    return await RefuseAsync($"jobs: '{asked.RunId}' is not a run's id.").ConfigureAwait(false);
                }

                return await AnswerAsync(await console.RerunAsync(runId, asked.Reason, operatorName, cancellationToken).ConfigureAwait(false), run => $"Queued the run {run.Id:D} of {run.Job} again.").ConfigureAwait(false);
            default:
                return await RefuseAsync($"jobs: the action '{asked.Action}' is none of {List}, {Run}, {Pause}, {Resume} and {Rerun}.").ConfigureAwait(false);
        }
    }

    private async Task<int> ListAsync(CancellationToken cancellationToken)
    {
        var jobs = await console.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in jobs.Where(j => asked.Job is not { Length: > 0 } || j.Name == asked.Job))
        {
            var last = job.LastRun is { } run ? $"{run.Status}@{Time(run.FinishedAt)}({run.DurationMs ?? 0}ms)" : "none";
            var failure = job.LastFailure is { } failed ? $" last_failure={Time(failed.FinishedAt)}:{failed.Error}" : string.Empty;
            await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{job.Name} {job.State} {job.Schedule.Replace(' ', '_')} {job.Priority.ToString().ToLowerInvariant()} next={Time(job.NextRunAt)} last={last} queued={job.Queued} running={job.Running} dead={job.DeadLetters}{failure}")).ConfigureAwait(false);
        }

        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Listed: {jobs.Count} job(s).")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> AnswerAsync<T>(Result<T> result, Func<T, string> words)
    {
        if (result.IsFailure)
        {
            return await RefuseAsync($"jobs: {result.Error.Code}: {result.Error.Message}").ConfigureAwait(false);
        }

        await Console.Out.WriteLineAsync(words(result.Value)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RefuseAsync(string message)
    {
        await Console.Error.WriteLineAsync(message).ConfigureAwait(false);
        return 1;
    }

    private static string Time(DateTimeOffset? at) => at?.ToString("u", CultureInfo.InvariantCulture).Replace(' ', 'T') ?? "-";
}

/// <summary>
/// A record for every job the unit declares (TF-124 AC1): a new tenant's seed makes them, active, each scheduled from now,
/// and the runner's first tick in a process makes those a newer build brought.
/// </summary>
public sealed class JobsSeeder(JobsDbContext context, IUnitOfWork unitOfWork, JobDeclarations declarations, TimeProvider time, ISettings? settings = null) : ISeeder
{
    public int Order => 900;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var zone = await JobZone.OfAsync(settings, cancellationToken).ConfigureAwait(false);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await JobRecordsSync.EnsureAsync(context, declarations, zone, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>jobs.prune_runs</c> (ADR-0061): the finished runs older than the tenant's history (<c>jobs.history.days</c>) go, a
/// batch at a time; a dead letter stays until someone runs it again, and so does every run still owed.
/// </summary>
public sealed class PruneRunsJob(JobsDbContext context, ISettings settings, TimeProvider time) : IJob
{
    public const int Batch = 5000;

    public static readonly JobDefinition Definition = JobDefinition.Create<PruneRunsJob>(
        "jobs.prune_runs", "Removes finished job runs older than the tenant's history; dead letters stay.", "Platform team",
        JobSchedule.Interval(TimeSpan.FromHours(1), firstAfter: TimeSpan.FromMinutes(5)), PriorityClass.Bulk, TimeSpan.FromMinutes(10));

    public async Task<Result> RunAsync(JobContext run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var days = await settings.GetAsync(JobsSettings.HistoryDays, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var cutoff = time.GetUtcNow().AddDays(-days);
        var removed = 0;
        int batch;
        do
        {
            var ids = await context.Runs.Where(r => (r.Status == RunStates.Succeeded || r.Status == RunStates.Skipped) && r.FinishedAt < cutoff)
                .OrderBy(r => r.FinishedAt).Select(r => r.Id).Take(Batch).ToListAsync(cancellationToken).ConfigureAwait(false);
            batch = ids.Count == 0 ? 0 : await context.Runs.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            removed += batch;
        }
        while (batch == Batch);

        run.Log(string.Create(CultureInfo.InvariantCulture, $"Removed {removed} finished run(s) older than {days} days."));
        return Result.Success();
    }
}
