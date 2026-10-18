using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Services;

public sealed record NewTenant(string Slug, string DisplayName, IReadOnlyList<string> Hostnames, string AdministratorLoginId, string? AdministratorDisplayName, SizeClass SizeClass, string? Region, string? Plan);

/// <summary>
/// The tenant registry (TF-001): create with unique slug and hostnames, read, change status, and ask the fleet to
/// provision (TF-002). A tenant and its hostnames are written with the audit event in one transaction.
/// </summary>
public sealed class TenantRegistry(
    TenancyDbContext context,
    TenancyOptions options,
    IProvisioningJobs jobs,
    IUnitOfWork unitOfWork,
    TimeProvider time,
    IAuditWriter audit,
    ILogger<TenantRegistry> logger) : ITenantRegistry
{
    public async Task<Result<TenantRecord>> CreateAsync(NewTenant request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TenantId.TryParse(request.Slug, out var tenantId))
        {
            return Error.Validation("tenancy.tenant.slug_invalid", $"'{request.Slug}' is not a tenant identifier: lowercase letters, digits and hyphens, starting with a letter.", new FieldError("slug", "invalid", "Use lowercase letters, digits and hyphens."));
        }

        IReadOnlyList<string> requested = request.Hostnames.Count == 0 ? new[] { $"{tenantId.Value}.{options.BaseDomain}" } : request.Hostnames;
        var hostnames = requested.Select(TenantHostname.Normalise).Distinct(StringComparer.Ordinal).ToList();
        var invalid = hostnames.FirstOrDefault(h => !TenantHostname.IsValid(h));
        if (invalid is not null)
        {
            return Error.Validation(TenancyErrors.HostnameInvalid, $"'{invalid}' is not a valid hostname.", new FieldError("hostnames", "invalid", $"'{invalid}' is not a valid hostname."));
        }

        if (await context.Tenants.AnyAsync(t => t.Slug == tenantId.Value, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(TenancyErrors.SlugTaken, $"A tenant with the identifier '{tenantId.Value}' exists already.");
        }

        var taken = await context.Hostnames.AsNoTracking().Where(h => hostnames.Contains(h.Hostname)).Select(h => h.Hostname).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (taken is not null)
        {
            return Error.Conflict(TenancyErrors.HostnameTaken, $"The hostname '{taken}' belongs to another tenant.");
        }

        var tenant = new Tenant(tenantId, request.DisplayName, request.Region ?? options.DefaultRegion, request.Plan ?? options.DefaultPlan, request.SizeClass, request.AdministratorLoginId, request.AdministratorDisplayName);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.Tenants.Add(tenant);
        context.Hostnames.AddRange(hostnames.Select(h => new TenantHostname(tenant.Id, h)));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.TenantCreated, AuditOutcome.Success, tenant.CreatedBy, tenant.Slug, new Dictionary<string, string>(StringComparer.Ordinal) { ["hostnames"] = string.Join(",", hostnames), ["size_class"] = tenant.SizeClass.ToString().ToLowerInvariant() }, "tenant", tenant.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Tenant {Slug} created with {Count} hostname(s).", tenant.Slug, hostnames.Count);
        return ToRecord(tenant, hostnames);
    }

    public async Task<TenantRecord?> FindAsync(string slug, CancellationToken cancellationToken)
    {
        if (!TenantId.TryParse(slug, out var tenantId))
        {
            return null;
        }

        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == tenantId.Value, cancellationToken).ConfigureAwait(false);
        return tenant is null ? null : ToRecord(tenant, await HostnamesOfAsync(tenant.Id, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<string>> HostnamesOfAsync(Guid tenantRowId, CancellationToken cancellationToken) =>
        await context.Hostnames.AsNoTracking().Where(h => h.TenantId == tenantRowId).OrderBy(h => h.CreatedAt).ThenBy(h => h.Hostname).Select(h => h.Hostname).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<Result> SetStatusAsync(string slug, TenantStatus status, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        var previous = tenant.Status;
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        tenant.SetStatus(status, time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.StatusChanged, AuditOutcome.Success, tenant.UpdatedBy, tenant.Slug, new Dictionary<string, string>(StringComparer.Ordinal) { ["from"] = previous.ToString().ToLowerInvariant(), ["to"] = status.ToString().ToLowerInvariant() }, "tenant", tenant.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>TF-002: a job for a tenant in Provisioning (or Failed earlier); an Active tenant is refused, so is any other status.</summary>
    public async Task<Result<ProvisioningJobInfo>> RequestProvisioningAsync(string slug, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        if (tenant.Status == TenantStatus.Active)
        {
            return Error.Conflict(TenancyErrors.AlreadyActive, $"Tenant '{slug}' is active already; provisioning does not run again.");
        }

        if (tenant.Status != TenantStatus.Provisioning)
        {
            return Error.Conflict(TenancyErrors.NotProvisionable, $"Tenant '{slug}' is {tenant.Status.ToString().ToLowerInvariant()} and cannot be provisioned.");
        }

        return await RequestJobAsync(tenant, JobKind.Provision, TenancyAuditEvents.ProvisioningRequested, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>TF-210 (S1): the unit goes, the data stays. Only an Active tenant can be de-provisioned.</summary>
    public async Task<Result<ProvisioningJobInfo>> RequestDeprovisioningAsync(string slug, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        if (tenant.Status != TenantStatus.Active)
        {
            return Error.Conflict(TenancyErrors.NotProvisionable, $"Tenant '{slug}' is {tenant.Status.ToString().ToLowerInvariant()}; only an active tenant can be de-provisioned.");
        }

        return await RequestJobAsync(tenant, JobKind.Deprovision, TenancyAuditEvents.DeprovisioningRequested, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-render and re-apply an Active tenant's unit (TF-210): a new image tag or template reaches it this way.</summary>
    public async Task<Result<ProvisioningJobInfo>> RequestUpdateAsync(string slug, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        if (tenant.Status != TenantStatus.Active)
        {
            return Error.Conflict(TenancyErrors.NotProvisionable, $"Tenant '{slug}' is {tenant.Status.ToString().ToLowerInvariant()}; only an active tenant's unit can be updated.");
        }

        return await RequestJobAsync(tenant, JobKind.Update, TenancyAuditEvents.UpdateRequested, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<ProvisioningJobInfo>> RequestJobAsync(Tenant tenant, JobKind kind, string auditEvent, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var job = await jobs.RequestAsync(tenant.Slug, kind, cancellationToken).ConfigureAwait(false);
        if (job.IsFailure)
        {
            return job.Error;
        }

        await audit.WriteAsync(new AuditEvent(auditEvent, AuditOutcome.Success, tenant.UpdatedBy, tenant.Slug, new Dictionary<string, string>(StringComparer.Ordinal) { ["job_id"] = job.Value.Id.ToString("D"), ["kind"] = kind.ToString().ToLowerInvariant() }, "tenant", tenant.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("{Kind} requested for tenant {Slug}: job {JobId}.", kind, tenant.Slug, job.Value.Id);
        return job.Value;
    }

    public static TenantRecord ToRecord(Tenant tenant, IReadOnlyList<string> hostnames)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return new TenantRecord(tenant.Id, tenant.Slug, tenant.DisplayName, hostnames, tenant.Region, tenant.Plan, tenant.SizeClass, tenant.Status, tenant.AdministratorLoginId, tenant.AdministratorDisplayName, tenant.DatabaseName, tenant.StorageContainer, tenant.CreatedAt, tenant.ActivatedAt);
    }
}
