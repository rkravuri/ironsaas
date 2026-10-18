using Iron.Core.Results;
using Iron.Core.Tenancy;
using Iron.Fleet.Contracts;
using Iron.Fleet.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Fleet.Services;

public sealed class ProvisioningJobs(FleetDbContext context, TimeProvider time) : IProvisioningJobs
{
    public async Task<Result<ProvisioningJobInfo>> RequestAsync(string tenantSlug, CancellationToken cancellationToken)
    {
        if (!TenantId.TryParse(tenantSlug, out var tenant))
        {
            return Error.Validation("fleet.tenant.invalid", $"'{tenantSlug}' is not a tenant identifier.");
        }

        var open = await context.ProvisioningJobs
            .Where(j => j.TenantSlug == tenant.Value && (j.Status == ProvisioningStatus.Requested || j.Status == ProvisioningStatus.Running))
            .OrderByDescending(j => j.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (open is not null)
        {
            return Error.Conflict(FleetErrors.JobInProgress, $"Provisioning job {open.Id} for '{tenant.Value}' is {open.Status.ToString().ToLowerInvariant()}.");
        }

        var job = new ProvisioningJob(tenant, time.GetUtcNow());
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
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return job?.ToInfo();
    }
}
