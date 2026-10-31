using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>What the operator does to a tenant's job: start, pause or resume a job by name, or run a run again, with why.</summary>
public sealed record OperatorJobAction(string Action, string? Job, Guid? RunId, string Reason);

/// <summary>
/// The operator's view and control of a tenant's jobs (TF-121, ADR-0061): the fleet runs the unit's <c>jobs</c> command
/// with what was asked, and the job's output is the answer. Every action but the list needs a reason, which both trails
/// keep with the operator's name; the list is recorded too, since it reads a tenant's runs and their errors.
/// </summary>
public sealed class OperatorJobs(TenancyDbContext context, IProvisioningJobs jobs, IUnitOfWork unitOfWork, IAuditWriter audit, ICurrentPrincipal actor, OperatorNames names)
{
    public const string Run = "run";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Rerun = "rerun";

    /// <summary>The actions the unit's <c>jobs</c> command takes beside its list.</summary>
    public static readonly IReadOnlyList<string> Actions = [Run, Pause, Resume, Rerun];

    public Task<Result<ProvisioningJobInfo>> ListAsync(string slug, string? job, CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["Jobs__Command__Action"] = "list" };
        if (job is { Length: > 0 } name)
        {
            environment["Jobs__Command__Job"] = name;
        }

        return RequestAsync(slug, job ?? "all", null, environment, TenancyAuditEvents.JobsListed, AuditSeverity.Normal, cancellationToken);
    }

    public Task<Result<ProvisioningJobInfo>> ActAsync(string slug, OperatorJobAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var why = action.Reason?.Trim();
        if (string.IsNullOrEmpty(why))
        {
            return Task.FromResult<Result<ProvisioningJobInfo>>(Error.Validation(TenancyErrors.JobsReasonRequired, "Say why you act on the tenant's job: both audit trails keep it.", new FieldError("reason", "required", "Say why.")));
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Jobs__Command__Action"] = action.Action,
            ["Jobs__Command__Reason"] = why,
        };
        if (action.Job is { Length: > 0 } job)
        {
            environment["Jobs__Command__Job"] = job;
        }

        if (action.RunId is { } run)
        {
            environment["Jobs__Command__RunId"] = run.ToString("D");
        }

        var subject = action.Job is { Length: > 0 } ? action.Job : action.RunId?.ToString("D") ?? action.Action;
        return RequestAsync(slug, subject, why, environment, TenancyAuditEvents.JobsActionRequested, AuditSeverity.High, cancellationToken);
    }

    private async Task<Result<ProvisioningJobInfo>> RequestAsync(string slug, string subject, string? reason, Dictionary<string, string> environment, string action, string severity, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        if (tenant.Status != TenantStatus.Active)
        {
            return Error.Conflict(TenancyErrors.NotProvisionable, $"Tenant '{slug}' is {tenant.Status.ToString().ToLowerInvariant()}; only an active tenant's jobs can be listed or acted on.");
        }

        var name = await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        environment["Jobs__Command__Operator"] = name;
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var job = await jobs.RequestAsync(tenant.Slug, JobKind.Jobs, new JobRequest(subject, reason ?? "list", name) { Environment = environment }, cancellationToken).ConfigureAwait(false);
        if (job.IsFailure)
        {
            return job.Error;
        }

        var details = environment.Where(e => e.Key != "Jobs__Command__Reason").ToDictionary(e => e.Key["Jobs__Command__".Length..].ToLowerInvariant(), e => e.Value, StringComparer.Ordinal);
        details["job_id"] = job.Value.Id.ToString("D");
        await audit.WriteAsync(new AuditEvent(action, AuditOutcome.Success, actor.Id, tenant.Slug, details, "tenant", tenant.Id, reason, Severity: severity), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job.Value;
    }
}
