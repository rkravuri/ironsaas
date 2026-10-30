using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>A suite to add to a tenant: which, under which of its plans (its only plan when none is named), from and until when, and why.</summary>
public sealed record SuiteAddition(string Suite, string? Plan, DateOnly? StartsOn, DateOnly? EndsOn, string? Reason);

/// <summary>What adding or removing a suite did: the suite's entitlements before and after, and the update job that installs or switches it off.</summary>
public sealed record SuiteChange(string Suite, PlanPreview Entitlements, ProvisioningJobInfo Job);

/// <summary>
/// A tenant's suites (TF-244, ADR-0059): the control plane records which suites a tenant has and under which plan, and
/// the fleet installs them in its unit. Adding a suite to an active tenant applies the suite's plan and requests an update
/// job, which migrates and seeds the suite's schemas and restarts the unit with it; removing one takes the suite's
/// entitlements away and requests an update job that restarts the unit without it, while its schemas and data stay. Both
/// need the operator's reason, and the audit event, the history and the job are written in one transaction (ADR-0013).
/// </summary>
public sealed class SuiteService(
    TenancyDbContext context,
    EntitlementService entitlements,
    IProvisioningJobs jobs,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    OperatorNames names,
    ITenantEntitlements publisher,
    ITenantFlags flags,
    ILogger<SuiteService> logger)
{
    public const int ReasonMaxLength = 500;

    /// <summary>The suites a tenant has, with their plans.</summary>
    public async Task<Result<IReadOnlyList<TenantSuite>>> ListAsync(string slug, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        return tenant is null
            ? Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.")
            : Result.Success(await entitlements.SuitesAsync(tenant.Id, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>TF-244 AC3: the suite and its plan for an active tenant, and the update job that installs it.</summary>
    public async Task<Result<SuiteChange>> AddAsync(string slug, SuiteAddition addition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addition);
        var why = Reason(addition.Reason);
        if (why.IsFailure)
        {
            return why.Error;
        }

        var suite = addition.Suite.Trim().ToLowerInvariant();
        var plan = await entitlements.SuitePlanAsync(suite, addition.Plan, cancellationToken).ConfigureAwait(false);
        if (plan.IsFailure)
        {
            return plan.Error;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var tenant = await ActiveAsync(slug, "added to", cancellationToken).ConfigureAwait(false);
        if (tenant.IsFailure)
        {
            return tenant.Error;
        }

        var applied = await entitlements.AddSuiteWithinAsync(tenant.Value, suite, new PlanApplication(plan.Value, addition.StartsOn, addition.EndsOn, why.Value), cancellationToken).ConfigureAwait(false);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        var change = await RequestUpdateAsync(tenant.Value, suite, TenancyAuditEvents.SuiteAdded, applied.Value, why.Value, cancellationToken).ConfigureAwait(false);
        if (change.IsFailure)
        {
            return change.Error;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await PublishAsync(tenant.Value.Slug, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Suite {Suite} added to tenant {Slug} with the plan {Plan}: job {JobId}.", suite, tenant.Value.Slug, plan.Value, change.Value.Job.Id);
        return change;
    }

    /// <summary>TF-244 AC4: the suite's entitlements go and the update job restarts the unit without it; its data stays.</summary>
    public async Task<Result<SuiteChange>> RemoveAsync(string slug, string suite, string? reason, CancellationToken cancellationToken)
    {
        var why = Reason(reason);
        if (why.IsFailure)
        {
            return why.Error;
        }

        var name = suite.Trim().ToLowerInvariant();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var tenant = await ActiveAsync(slug, "removed from", cancellationToken).ConfigureAwait(false);
        if (tenant.IsFailure)
        {
            return tenant.Error;
        }

        var removed = await entitlements.RemoveSuiteWithinAsync(tenant.Value, name, why.Value, cancellationToken).ConfigureAwait(false);
        if (removed.IsFailure)
        {
            return removed.Error;
        }

        var change = await RequestUpdateAsync(tenant.Value, name, TenancyAuditEvents.SuiteRemoved, removed.Value, why.Value, cancellationToken).ConfigureAwait(false);
        if (change.IsFailure)
        {
            return change.Error;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await PublishAsync(tenant.Value.Slug, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Suite {Suite} removed from tenant {Slug}: job {JobId}.", name, tenant.Value.Slug, change.Value.Job.Id);
        return change;
    }

    private static Result<string> Reason(string? reason)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why))
        {
            return Error.Validation(TenancyErrors.SuiteReasonRequired, "Say why the suite is added or removed: the audit trail keeps it with your name.", new FieldError("reason", "required", "Say why."));
        }

        return why.Length > ReasonMaxLength ? why[..ReasonMaxLength] : why;
    }

    /// <summary>A suite is added to or removed from an active tenant's unit; a new tenant gets its suites when it is created.</summary>
    private async Task<Result<Tenant>> ActiveAsync(string slug, string verb, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        return tenant.Status == TenantStatus.Active
            ? tenant
            : Error.Conflict(TenancyErrors.NotProvisionable, $"Tenant '{slug}' is {tenant.Status.ToString().ToLowerInvariant()}; a suite is {verb} an active tenant only. A new tenant is created with its suites (make provision SUITES=...).");
    }

    /// <summary>The update job that brings the unit to the tenant's suites, and the audit event naming the operator, the suite, the plan and the job.</summary>
    private async Task<Result<SuiteChange>> RequestUpdateAsync(Tenant tenant, string suite, string auditEvent, PlanPreview entitlementChange, string reason, CancellationToken cancellationToken)
    {
        var operatorName = await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        var job = await jobs.RequestAsync(tenant.Slug, JobKind.Update, new JobRequest(suite, reason, operatorName), cancellationToken).ConfigureAwait(false);
        if (job.IsFailure)
        {
            return job.Error;
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["suite"] = suite,
            ["plan"] = auditEvent == TenancyAuditEvents.SuiteAdded ? entitlementChange.PlanAfter : entitlementChange.PlanBefore,
            ["operator"] = operatorName,
            ["job_id"] = job.Value.Id.ToString("D"),
            ["revision"] = entitlementChange.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        await audit.WriteAsync(new AuditEvent(auditEvent, AuditOutcome.Success, actor.Id, tenant.Slug, details, "tenant", tenant.Id, reason, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        return new SuiteChange(suite, entitlementChange, job.Value);
    }

    /// <summary>The store's copy follows the commit (ADR-0034); a store that cannot take it now is reconciled within a minute.</summary>
    private async Task PublishAsync(string slug, CancellationToken cancellationToken)
    {
        await publisher.PublishAsync(slug, cancellationToken).ConfigureAwait(false);
        await flags.PublishAsync(slug, cancellationToken).ConfigureAwait(false);
    }
}
