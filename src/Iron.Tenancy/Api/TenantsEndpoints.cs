using FluentValidation;
using Iron.Api.Lists;
using Iron.Api.Permissions;
using Iron.Api.Responses;
using Iron.Api.Routing;
using Iron.Api.Validation;
using Iron.Core.Paging;
using Iron.Core.Results;
using Iron.Core.Tenancy;
using Iron.Data.Lists;
using Iron.Fleet.Contracts;
using Iron.Tenancy.Contracts;
using Iron.Tenancy.Data;
using Iron.Tenancy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Tenancy.Api;

public sealed record CreateTenantRequest(
    string Slug,
    string DisplayName,
    IReadOnlyList<string>? Hostnames,
    string AdministratorLoginId,
    string? AdministratorDisplayName,
    SizeClass? SizeClass,
    string? Region,
    string? Plan);

public sealed class CreateTenantRequestValidator : AbstractValidator<CreateTenantRequest>
{
    public CreateTenantRequestValidator()
    {
        RuleFor(r => r.Slug).NotEmpty().Must(slug => TenantId.TryParse(slug, out _)).WithMessage("Use lowercase letters, digits and hyphens, starting with a letter.");
        RuleFor(r => r.DisplayName).NotEmpty().MaximumLength(Tenant.DisplayNameMaxLength);
        RuleFor(r => r.AdministratorLoginId).NotEmpty().MaximumLength(254).Must(login => login.Contains('@', StringComparison.Ordinal)).WithMessage("The first administrator is identified by an email address.");
        RuleFor(r => r.AdministratorDisplayName).MaximumLength(200);
        RuleFor(r => r.Region).MaximumLength(32);
        RuleFor(r => r.Plan).MaximumLength(64);
        RuleForEach(r => r.Hostnames).NotEmpty().MaximumLength(TenantHostname.MaxLength);
    }
}

public sealed record ProvisioningResponse(
    Guid JobId,
    JobKind Kind,
    ProvisioningStatus Status,
    string? CurrentStep,
    string? Error,
    string? Output,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<ProvisioningStepInfo> Steps)
{
    public static ProvisioningResponse From(ProvisioningJobInfo job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new ProvisioningResponse(job.Id, job.Kind, job.Status, job.CurrentStep, job.Error, job.Output, job.RequestedAt, job.StartedAt, job.FinishedAt, job.Steps);
    }
}

/// <summary>The control plane's tenant API (TF-001, TF-002): create, list, get, request provisioning, see the latest job.</summary>
internal static class TenantsEndpoints
{
    /// <summary>The whitelist of the list grammar.</summary>
    public static readonly ListFields<Tenant> Fields = new ListFields<Tenant>()
        .Add("id", t => t.Id, FieldUse.Select | FieldUse.Filter)
        .Add("slug", t => t.Slug)
        .Add("display_name", t => t.DisplayName)
        .Add("status", t => t.Status, FieldUse.Select | FieldUse.Filter)
        .Add("size_class", t => t.SizeClass, FieldUse.Select | FieldUse.Filter)
        .Add("region", t => t.Region, FieldUse.Select | FieldUse.Filter)
        .Add("plan", t => t.Plan, FieldUse.Select | FieldUse.Filter)
        .Add("administrator_login_id", t => t.AdministratorLoginId, FieldUse.Select)
        .Add("database_name", t => t.DatabaseName, FieldUse.Select)
        .Add("storage_container", t => t.StorageContainer, FieldUse.Select)
        .Add("created_at", t => t.CreatedAt, FieldUse.Select | FieldUse.Sort)
        .Add("activated_at", t => t.ActivatedAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("slug");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var tenants = endpoints.MapApiGroup("tenants");
        tenants.MapGet("/", ListAsync).RequirePermission(TenancyPermissions.TenantsRead).WithName("ListTenants").WithSummary("List the tenants of the platform.");
        tenants.MapGet("/{slug}", GetAsync).RequirePermission(TenancyPermissions.TenantsRead).WithName("GetTenant").WithSummary("One tenant with its hostnames.");
        tenants.MapPost("/", CreateAsync).RequirePermission(TenancyPermissions.TenantsManage).Validate<CreateTenantRequest>().WithName("CreateTenant").WithSummary("Register a tenant in Provisioning status.");
        tenants.MapPost("/{slug}/provision", ProvisionAsync).RequirePermission(TenancyPermissions.TenantsProvision).WithName("ProvisionTenant").WithSummary("Ask the fleet to provision the tenant.");
        tenants.MapPost("/{slug}/deprovision", DeprovisionAsync).RequirePermission(TenancyPermissions.TenantsProvision).WithName("DeprovisionTenant").WithSummary("Ask the fleet to take the unit down; data is kept.");
        tenants.MapGet("/{slug}/provisioning", ProvisioningAsync).RequirePermission(TenancyPermissions.TenantsRead).WithName("GetProvisioning").WithSummary("The latest job of the tenant.");
    }

    private static async Task<IResult> ListAsync(HttpContext http, TenancyDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.Tenants.AsNoTracking().ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        var ids = page.Items.Select(t => t.Id).ToList();
        var rows = await context.Hostnames.AsNoTracking()
            .Where(h => ids.Contains(h.TenantId))
            .Select(h => new { h.TenantId, h.Hostname })
            .ToListAsync(cancellationToken);
        var hostnames = rows.GroupBy(h => h.TenantId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(h => h.Hostname).Order(StringComparer.Ordinal).ToList());
        return ApiResults.List(page.Map(tenant => TenantRegistry.ToRecord(tenant, hostnames.GetValueOrDefault(tenant.Id, []))), http, query);
    }

    private static async Task<IResult> GetAsync(string slug, HttpContext http, TenantRegistry registry, CancellationToken cancellationToken)
    {
        var tenant = await registry.FindAsync(slug, cancellationToken);
        return tenant is null ? ApiResults.Problem(Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'."), http) : TypedResults.Ok(tenant);
    }

    private static async Task<IResult> CreateAsync(CreateTenantRequest request, HttpContext http, TenantRegistry registry, CancellationToken cancellationToken)
    {
        var result = await registry.CreateAsync(
            new NewTenant(request.Slug, request.DisplayName, request.Hostnames ?? [], request.AdministratorLoginId, request.AdministratorDisplayName, request.SizeClass ?? SizeClass.Small, request.Region, request.Plan),
            cancellationToken);
        return ApiResults.From(result, http, tenant => TypedResults.Created($"{ApiRouting.Prefix}/tenants/{tenant.Slug}", tenant));
    }

    private static async Task<IResult> ProvisionAsync(string slug, HttpContext http, TenantRegistry registry, CancellationToken cancellationToken)
    {
        var result = await registry.RequestProvisioningAsync(slug, cancellationToken);
        return ApiResults.From(result, http, job => TypedResults.Accepted($"{ApiRouting.Prefix}/tenants/{slug}/provisioning", ProvisioningResponse.From(job)));
    }

    private static async Task<IResult> DeprovisionAsync(string slug, HttpContext http, TenantRegistry registry, CancellationToken cancellationToken)
    {
        var result = await registry.RequestDeprovisioningAsync(slug, cancellationToken);
        return ApiResults.From(result, http, job => TypedResults.Accepted($"{ApiRouting.Prefix}/tenants/{slug}/provisioning", ProvisioningResponse.From(job)));
    }

    private static async Task<IResult> ProvisioningAsync(string slug, HttpContext http, IProvisioningJobs jobs, TenantRegistry registry, CancellationToken cancellationToken)
    {
        if (await registry.FindAsync(slug, cancellationToken) is null)
        {
            return ApiResults.Problem(Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'."), http);
        }

        var job = await jobs.LatestAsync(slug, cancellationToken);
        return job is null
            ? ApiResults.Problem(Error.NotFound("fleet.job.not_found", $"Tenant '{slug}' has no provisioning job yet."), http)
            : TypedResults.Ok(ProvisioningResponse.From(job));
    }
}
