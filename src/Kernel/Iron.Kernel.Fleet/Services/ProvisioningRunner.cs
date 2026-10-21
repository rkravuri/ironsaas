using System.Diagnostics;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Fleet.Data;
using Iron.Kernel.Fleet.Services.Steps;
using Iron.Kernel.Tenancy.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Fleet.Services;

/// <summary>
/// Runs one provisioning job (TF-002): the steps in order, each recorded with its timing, a failure stopping the job at
/// that step, and a later run continuing from it. Completed steps of a reopened job are skipped. An Active tenant is
/// refused here as well as at the API.
/// </summary>
public sealed class ProvisioningRunner(
    FleetDbContext context,
    IEnumerable<IProvisioningStep> steps,
    ITenantRegistry tenants,
    FleetOptions options,
    TimeProvider time,
    IAuditWriter audit,
    ICurrentPrincipal principal,
    ILogger<ProvisioningRunner> logger)
{
    public const string JobStartedEvent = "fleet.provisioning_started";
    public const string JobCompletedEvent = "fleet.provisioning_completed";
    public const string JobFailedEvent = "fleet.provisioning_failed";

    public async Task<ProvisioningJobInfo> RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await context.ProvisioningJobs.SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"There is no provisioning job {jobId}.");
        var tenant = await tenants.FindAsync(job.TenantSlug, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job {jobId} names tenant '{job.TenantSlug}', which is not in the registry.");

        if (job.Kind == JobKind.Provision && tenant.Status == TenantStatus.Active)
        {
            job.Fail(time.GetUtcNow(), $"Tenant '{tenant.Slug}' is active already; provisioning does not run again.");
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return job.ToInfo(await StepsAsync(job.Id, cancellationToken).ConfigureAwait(false));
        }

        var ordered = steps.Where(s => s.Kinds.Contains(job.Kind)).OrderBy(s => s.Order).ToList();
        var records = await EnsureStepRecordsAsync(job, ordered, cancellationToken).ConfigureAwait(false);
        job.Start(time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(JobStartedEvent, AuditOutcome.Success, principal.Id, tenant.Slug, null, "provisioning_job", job.Id), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Provisioning job {JobId} for tenant {Slug} started.", job.Id, tenant.Slug);

        if (job.Kind == JobKind.Recover && tenant.Status != TenantStatus.Active)
        {
            job.Fail(time.GetUtcNow(), $"Tenant '{tenant.Slug}' is {tenant.Status.ToString().ToLowerInvariant()}; only an active tenant's administrator can be recovered.");
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return job.ToInfo(await StepsAsync(job.Id, cancellationToken).ConfigureAwait(false));
        }

        var provisioning = new ProvisioningContext(tenant, options, job.Request, job.Id);
        string? lastOutput = null;
        foreach (var step in ordered)
        {
            var record = records[step.Name];
            if (record.Status == StepStatus.Completed)
            {
                logger.LogInformation("Job {JobId}: step {Step} was completed in an earlier run; skipped.", job.Id, step.Name);
                lastOutput = record.Output ?? lastOutput;
                continue;
            }

            job.StepStarted(step.Name);
            record.Start(time.GetUtcNow());
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var output = await step.RunAsync(provisioning, cancellationToken).ConfigureAwait(false);
                record.Complete(time.GetUtcNow(), output);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                lastOutput = output ?? lastOutput;
                logger.LogInformation("Job {JobId}: step {Step} completed in {Elapsed} ms. {Output}", job.Id, step.Name, stopwatch.ElapsedMilliseconds, output ?? string.Empty);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var error = $"{exception.GetType().Name}: {exception.Message}";
                record.Fail(time.GetUtcNow(), error);
                job.Fail(time.GetUtcNow(), $"step {step.Name} failed: {exception.Message}");
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                var reason = exception.Message.Length > 500 ? exception.Message[..500] : exception.Message;
                await audit.WriteAsync(new AuditEvent(JobFailedEvent, AuditOutcome.Failure, principal.Id, tenant.Slug, new Dictionary<string, string>(StringComparer.Ordinal) { ["step"] = step.Name }, "provisioning_job", job.Id, reason), cancellationToken).ConfigureAwait(false);
                logger.LogError(exception, "Job {JobId}: step {Step} failed after {Elapsed} ms.", job.Id, step.Name, stopwatch.ElapsedMilliseconds);
                return job.ToInfo(await StepsAsync(job.Id, cancellationToken).ConfigureAwait(false));
            }
        }

        // A recovery job changes a person in the unit, not the tenant: its status stays as it is.
        if (job.Kind != JobKind.Recover)
        {
            var finalStatus = job.Kind == JobKind.Deprovision ? TenantStatus.Decommissioned : TenantStatus.Active;
            var moved = await tenants.SetStatusAsync(tenant.Slug, finalStatus, cancellationToken).ConfigureAwait(false);
            if (moved.IsFailure)
            {
                throw new InvalidOperationException($"The tenant could not be set {finalStatus}: {moved.Error.Message}");
            }
        }

        job.Complete(time.GetUtcNow(), lastOutput);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(JobCompletedEvent, AuditOutcome.Success, principal.Id, tenant.Slug, null, "provisioning_job", job.Id), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Provisioning job {JobId} for tenant {Slug} completed: {Output}", job.Id, tenant.Slug, lastOutput ?? string.Empty);
        return job.ToInfo(await StepsAsync(job.Id, cancellationToken).ConfigureAwait(false));
    }

    private async Task<Dictionary<string, ProvisioningStep>> EnsureStepRecordsAsync(ProvisioningJob job, IReadOnlyList<IProvisioningStep> ordered, CancellationToken cancellationToken)
    {
        var existing = await context.ProvisioningSteps.Where(s => s.JobId == job.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var records = existing.ToDictionary(s => s.Name, StringComparer.Ordinal);
        foreach (var step in ordered)
        {
            if (!records.ContainsKey(step.Name))
            {
                var record = new ProvisioningStep(job.Id, step.Name, step.Order);
                context.ProvisioningSteps.Add(record);
                records[step.Name] = record;
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return records;
    }

    private async Task<IReadOnlyList<ProvisioningStep>> StepsAsync(Guid jobId, CancellationToken cancellationToken) =>
        await context.ProvisioningSteps.AsNoTracking().Where(s => s.JobId == jobId).OrderBy(s => s.Order).ToListAsync(cancellationToken).ConfigureAwait(false);
}
