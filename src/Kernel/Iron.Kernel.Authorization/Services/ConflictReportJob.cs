using Iron.Kernel.Authorization.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// <c>authz.conflict_report</c> (TF-036 AC3, TF-124, ADR-0061): the conflicts report, two minutes after a unit first
/// schedules it, then once a day, so a rule written or changed shows its conflicts within a day. A run looks at the rights
/// as they are, so a repeated run reports the same.
/// </summary>
public sealed class ConflictReportJob(ConflictService conflicts, ILogger<ConflictReportJob> logger) : IJob
{
    public static readonly JobDefinition Definition = JobDefinition.Create<ConflictReportJob>(
        "authz.conflict_report", "Runs the conflicts report over everyone's rights against the conflict rules.", "Platform team",
        JobSchedule.Interval(TimeSpan.FromDays(1), firstAfter: TimeSpan.FromMinutes(2)), PriorityClass.Bulk, TimeSpan.FromHours(1));

    public async Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var run = await conflicts.RunReportAsync(ConflictReportTriggers.Daily, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("The conflicts report ran: {Rules} rule(s), {Conflicts} conflict(s).", run.Rules, run.Conflicts);
        context.Log($"{run.Rules} rule(s), {run.Conflicts} conflict(s).");
        return Result.Success();
    }
}
