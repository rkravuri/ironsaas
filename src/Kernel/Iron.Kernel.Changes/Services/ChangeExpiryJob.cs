using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Changes.Services;

/// <summary>
/// <c>changes.expire_pending</c> (TF-068, TF-124, ADR-0052, ADR-0061): ends the pending changes nobody decided within the
/// tenant's period, every five minutes. The lists and the decisions expire a change past its time as well, so a late run
/// never lets one apply; a repeated run finds them expired already.
/// </summary>
public sealed class ChangeExpiryJob(PendingChanges changes) : IJob
{
    public static readonly JobDefinition Definition = JobDefinition.Create<ChangeExpiryJob>(
        "changes.expire_pending", "Ends the pending configuration changes nobody decided within the tenant's period.", "Platform team",
        JobSchedule.Interval(TimeSpan.FromMinutes(5)), PriorityClass.Normal, TimeSpan.FromMinutes(5));

    public async Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        await changes.ExpireAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
