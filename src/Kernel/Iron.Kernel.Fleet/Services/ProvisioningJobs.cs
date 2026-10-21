using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Fleet.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Fleet.Services;

public sealed class ProvisioningJobs(FleetDbContext context, TimeProvider time) : IProvisioningJobs
{
    public Task<Result<ProvisioningJobInfo>> RequestAsync(string tenantSlug, JobKind kind, CancellationToken cancellationToken) =>
        RequestAsync(tenantSlug, kind, null, cancellationToken);

    public async Task<Result<ProvisioningJobInfo>> RequestAsync(string tenantSlug, JobKind kind, JobRequest? request, CancellationToken cancellationToken)
    {
        if (!TenantId.TryParse(tenantSlug, out var tenant))
        {
            return Error.Validation("fleet.tenant.invalid", $"'{tenantSlug}' is not a tenant identifier.");
        }

        var latest = await context.ProvisioningJobs
            .Where(j => j.TenantSlug == tenant.Value)
            .OrderByDescending(j => j.RequestedAt)
            .ThenByDescending(j => j.Id) // ids are UUID version 7: two jobs requested in the same instant keep their order
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is { IsOpen: true })
        {
            return Error.Conflict(FleetErrors.JobInProgress, $"Provisioning job {latest.Id} for '{tenant.Value}' is {latest.Status.ToString().ToLowerInvariant()}.");
        }

        if (request is null && latest is { Status: ProvisioningStatus.Failed, Request: null } && latest.Kind == kind)
        {
            latest.Reopen(time.GetUtcNow());
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return latest.ToInfo(await StepsOfAsync(latest.Id, cancellationToken).ConfigureAwait(false));
        }

        var job = new ProvisioningJob(tenant, kind, time.GetUtcNow(), request);
        context.ProvisioningJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return job.ToInfo();
    }

    public async Task<ProvisioningJobInfo?> LatestAsync(string tenantSlug, CancellationToken cancellationToken)
    {
        var slug = TenantId.TryParse(tenantSlug, out var tenant) ? tenant.Value : string.Empty;
        var job = await context.ProvisioningJobs.AsNoTracking()
            .Where(j => j.TenantSlug == slug)
            .OrderByDescending(j => j.RequestedAt)
            .ThenByDescending(j => j.Id) // ids are UUID version 7: two jobs requested in the same instant keep their order
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return job is null ? null : job.ToInfo(await StepsOfAsync(job.Id, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<ProvisioningStep>> StepsOfAsync(Guid jobId, CancellationToken cancellationToken) =>
        await context.ProvisioningSteps.AsNoTracking().Where(s => s.JobId == jobId).OrderBy(s => s.Order).ToListAsync(cancellationToken).ConfigureAwait(false);
}
