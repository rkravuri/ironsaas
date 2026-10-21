using System.Globalization;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Fleet.Data;
using Iron.Kernel.Security.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Fleet.Services;

/// <summary>Where a tenant's handover places are in the store (ADR-0046): one per job, under the tenant's path.</summary>
public static class HandoverPlaces
{
    public static string PathOf(string slug, Guid jobId) => $"tenants/{slug}/handover/{jobId:N}";

    public static string PatternOf(string slug) => $"tenants/{slug}/handover/*";
}

/// <summary>
/// Gives a job's handover once (ADR-0046): to the principal who asked for the job, after it completed, and only while
/// its link works; the place is deleted as it is given, or when it is found expired. Nobody else is given it, and it is
/// never given twice. The log lines say what happened, never the link.
/// </summary>
public sealed class JobHandovers(FleetDbContext context, ISecretStore secrets, TimeProvider time, ILogger<JobHandovers> logger) : IJobHandovers
{
    public async Task<JobHandoverAnswer> TakeAsync(Guid jobId, Guid askerId, CancellationToken cancellationToken)
    {
        var job = await context.ProvisioningJobs.SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);
        if (job is null || job.Status != ProvisioningStatus.Completed)
        {
            return new JobHandoverAnswer(HandoverState.None);
        }

        if (job.HandedOverAt is not null)
        {
            return new JobHandoverAnswer(HandoverState.Taken);
        }

        var path = HandoverPlaces.PathOf(job.TenantSlug, job.Id);
        var values = await secrets.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (values is null)
        {
            return new JobHandoverAnswer(HandoverState.None);
        }

        if (job.RequestedById != askerId)
        {
            return new JobHandoverAnswer(HandoverState.NotYours);
        }

        var item = new JobHandover(
            values.GetValueOrDefault(HandoverKeys.Kind) ?? string.Empty,
            values.GetValueOrDefault(HandoverKeys.Subject) ?? string.Empty,
            values.GetValueOrDefault(HandoverKeys.LinkPath) ?? string.Empty,
            DateTimeOffset.Parse(values.GetValueOrDefault(HandoverKeys.ExpiresAt) ?? "0001-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        await secrets.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        if (item.ExpiresAt <= time.GetUtcNow())
        {
            logger.LogInformation("The handover of job {JobId} ({Kind} for {Subject}) had expired; its place is deleted.", job.Id, item.Kind, item.Subject);
            return new JobHandoverAnswer(HandoverState.Expired);
        }

        job.HandedOver(time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("The handover of job {JobId} ({Kind} for {Subject}) was given to the principal who asked; its place is deleted.", job.Id, item.Kind, item.Subject);
        return new JobHandoverAnswer(HandoverState.Given, item);
    }
}
