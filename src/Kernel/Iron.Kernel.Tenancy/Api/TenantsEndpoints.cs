using FluentValidation;
using Iron.Kernel.Api.Http;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Tenancy.Api;

public sealed record CreateTenantRequest(
    string Slug,
    string DisplayName,
    IReadOnlyList<string>? Hostnames,
    string AdministratorLoginId,
    string? AdministratorDisplayName,
    SizeClass? SizeClass,
    string? Region,
    string? Plan,
    bool? SampleRecords = null,
    IReadOnlyList<SuiteChoiceRequest>? Suites = null,
    string? Reason = null);

/// <summary>A suite a new tenant starts with (TF-244), and its plan: the suite's only plan when none is named.</summary>
public sealed record SuiteChoiceRequest(string Suite, string? Plan);

/// <summary>Whether the tenant's unit runs the sample module, for testing (ADR-0057).</summary>
public sealed record SampleRecordsRequest(bool Enabled);

public sealed class CreateTenantRequestValidator : AbstractValidator<CreateTenantRequest>
{
    /// <summary>A slug is a DNS label: at most 63 characters.</summary>
    public const int TenantSlugMaxLength = 63;

    public CreateTenantRequestValidator()
    {
        RuleFor(r => r.Slug).NotEmpty().MaximumLength(TenantSlugMaxLength).Must(slug => TenantId.TryParse(slug, out _)).WithMessage("Use lowercase letters, digits and hyphens, starting with a letter.");
        RuleFor(r => r.DisplayName).NotEmpty().MaximumLength(Tenant.DisplayNameMaxLength);
        RuleFor(r => r.AdministratorLoginId).NotEmpty().MaximumLength(254).Must(login => login.Contains('@', StringComparison.Ordinal)).WithMessage("The first administrator is identified by an email address.");
        RuleFor(r => r.AdministratorDisplayName).MaximumLength(200);
        RuleFor(r => r.Region).MaximumLength(32);
        RuleFor(r => r.Plan).MaximumLength(64);
        RuleFor(r => r.Hostnames).MaximumCount(10);
        RuleForEach(r => r.Hostnames).NotEmpty().MaximumLength(TenantHostname.MaxLength);
        RuleFor(r => r.Reason).MaximumLength(SuiteService.ReasonMaxLength);
        RuleFor(r => r.Suites).MaximumCount(10);
        RuleForEach(r => r.Suites).ChildRules(suite =>
        {
            suite.RuleFor(s => s.Suite).NotEmpty().MaximumLength(Plan.SuiteMaxLength);
            suite.RuleFor(s => s.Plan).MaximumLength(64);
        });
    }
}

/// <summary>The operator's request for a tenant administrator's recovery link: why, and whom when not the first administrator.</summary>
/// <summary>The operator's recovery: why, whom (the first administrator unless named), and whether their second factors go too (ADR-0047).</summary>
public sealed record AdministratorRecoveryRequest(string Reason, string? LoginId, bool? ResetFactors = null);

public sealed class AdministratorRecoveryRequestValidator : AbstractValidator<AdministratorRecoveryRequest>
{
    public AdministratorRecoveryRequestValidator()
    {
        RuleFor(r => r.Reason).NotEmpty().WithMessage("Say why, in words the tenant's administrators will read.").MaximumLength(TenantRegistry.RecoveryReasonMaxLength);
        RuleFor(r => r.LoginId).MaximumLength(254);
    }
}

/// <summary>
/// A job's handover in the provisioning report (ADR-0046): its state, and once, to the operator who asked, the link with
/// the tenant's address, whose it is and until when. Afterwards the state says it was given.
/// </summary>
public sealed record HandoverResponse(string State, string? Kind = null, string? Subject = null, string? Link = null, DateTimeOffset? ExpiresAt = null, DateTimeOffset? HandedOverAt = null);

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
    IReadOnlyList<ProvisioningStepInfo> Steps,
    JobRequest? Request = null,
    HandoverResponse? Handover = null)
{
    public static ProvisioningResponse From(ProvisioningJobInfo job, HandoverResponse? handover = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new ProvisioningResponse(job.Id, job.Kind, job.Status, job.CurrentStep, job.Error, job.Output, job.RequestedAt, job.StartedAt, job.FinishedAt, job.Steps, job.Request, handover);
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
        tenants.MapGet("/", ListAsync).RequirePermission(TenancyPermissions.TenantsRead).Produces<ListResponse<TenantRecord>>(StatusCodes.Status200OK).WithName("ListTenants").WithSummary("List the tenants of the platform.");
        tenants.MapGet("/{slug}", GetAsync).RequirePermission(TenancyPermissions.TenantsRead).Produces<TenantRecord>(StatusCodes.Status200OK).WithName("GetTenant").WithSummary("One tenant with its hostnames.");
        tenants.MapPost("/", CreateAsync).RequirePermission(TenancyPermissions.TenantsManage).Validate<CreateTenantRequest>().Produces<TenantRecord>(StatusCodes.Status201Created).WithName("CreateTenant").WithSummary("Register a tenant in Provisioning status.");
        tenants.MapPost("/{slug}/provision", ProvisionAsync).RequirePermission(TenancyPermissions.TenantsProvision).Produces<ProvisioningResponse>(StatusCodes.Status202Accepted).WithName("ProvisionTenant").WithSummary("Ask the fleet to provision the tenant.");
        tenants.MapPut("/{slug}/sample-records", SampleRecordsAsync).RequirePermission(TenancyPermissions.TenantsManage).Produces<TenantRecord>(StatusCodes.Status200OK).WithName("SetTenantSampleRecords").WithSummary("Mark a tenant's unit to run the sample module for testing, or unmark it; the unit follows at its next provisioning or update.");
        tenants.MapPost("/{slug}/update", UpdateAsync).RequirePermission(TenancyPermissions.TenantsProvision).Produces<ProvisioningResponse>(StatusCodes.Status202Accepted).WithName("UpdateTenantUnit").WithSummary("Ask the fleet to re-render and re-apply the unit of an active tenant.");
        tenants.MapPost("/{slug}/deprovision", DeprovisionAsync).RequirePermission(TenancyPermissions.TenantsProvision).Produces<ProvisioningResponse>(StatusCodes.Status202Accepted).WithName("DeprovisionTenant").WithSummary("Ask the fleet to take the unit down; data is kept.");
        tenants.MapPost("/{slug}/administrator-recovery", RecoverAdministratorAsync).RequirePermission(TenancyPermissions.AdministratorsRecover).RateLimited(RateLimitPolicies.Recovery, "slug").Validate<AdministratorRecoveryRequest>().Produces<ProvisioningResponse>(StatusCodes.Status202Accepted).WithName("RecoverTenantAdministrator").WithSummary("Ask the fleet for a recovery link for a tenant's administrator, with a reason, and to reset their second factors when asked; the link is handed over once, valid 15 minutes and once.");
        tenants.MapGet("/{slug}/provisioning", ProvisioningAsync).RequirePermission(TenancyPermissions.TenantsRead).Produces<ProvisioningResponse>(StatusCodes.Status200OK).WithName("GetProvisioning").WithSummary("The latest job of the tenant; once it completed, the invitation or recovery link it handed over, given once to the operator who asked and then deleted.");
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
        var suiteRows = await context.Suites.AsNoTracking()
            .Where(s => ids.Contains(s.TenantId))
            .Select(s => new { s.TenantId, s.Suite })
            .ToListAsync(cancellationToken);
        var suites = suiteRows.GroupBy(s => s.TenantId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(s => s.Suite).Order(StringComparer.Ordinal).ToList());
        return ApiResults.List(page.Map(tenant => TenantRegistry.ToRecord(tenant, hostnames.GetValueOrDefault(tenant.Id, []), suites.GetValueOrDefault(tenant.Id, []))), http, query);
    }

    private static async Task<IResult> GetAsync(string slug, HttpContext http, TenantRegistry registry, CancellationToken cancellationToken)
    {
        var tenant = await registry.FindAsync(slug, cancellationToken);
        return tenant is null ? ApiResults.Problem(Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'."), http) : TypedResults.Ok(tenant);
    }

    private static async Task<IResult> CreateAsync(CreateTenantRequest request, HttpContext http, TenantRegistry registry, CancellationToken cancellationToken)
    {
        var result = await registry.CreateAsync(
            new NewTenant(request.Slug, request.DisplayName, request.Hostnames ?? [], request.AdministratorLoginId, request.AdministratorDisplayName, request.SizeClass ?? SizeClass.Small, request.Region, request.Plan, request.SampleRecords ?? false, request.Suites?.Select(s => new SuiteChoice(s.Suite, s.Plan)).ToList(), request.Reason),
            cancellationToken);
        return ApiResults.From(result, http, tenant => TypedResults.Created($"{ApiRouting.Prefix}/tenants/{tenant.Slug}", tenant));
    }

    private static async Task<IResult> SampleRecordsAsync(string slug, SampleRecordsRequest request, HttpContext http, TenantRegistry registry, CancellationToken cancellationToken) =>
        ApiResults.From(await registry.SetSampleRecordsAsync(slug, request.Enabled, cancellationToken), http, tenant => TypedResults.Ok(tenant));

    private static async Task<IResult> ProvisionAsync(string slug, HttpContext http, TenantRegistry registry, CancellationToken cancellationToken)
    {
        var result = await registry.RequestProvisioningAsync(slug, cancellationToken);
        return ApiResults.From(result, http, job => TypedResults.Accepted($"{ApiRouting.Prefix}/tenants/{slug}/provisioning", ProvisioningResponse.From(job)));
    }

    private static async Task<IResult> UpdateAsync(string slug, HttpContext http, TenantRegistry registry, CancellationToken cancellationToken)
    {
        var result = await registry.RequestUpdateAsync(slug, cancellationToken);
        return ApiResults.From(result, http, job => TypedResults.Accepted($"{ApiRouting.Prefix}/tenants/{slug}/provisioning", ProvisioningResponse.From(job)));
    }

    private static async Task<IResult> DeprovisionAsync(string slug, HttpContext http, TenantRegistry registry, CancellationToken cancellationToken)
    {
        var result = await registry.RequestDeprovisioningAsync(slug, cancellationToken);
        return ApiResults.From(result, http, job => TypedResults.Accepted($"{ApiRouting.Prefix}/tenants/{slug}/provisioning", ProvisioningResponse.From(job)));
    }

    private static async Task<IResult> RecoverAdministratorAsync(string slug, AdministratorRecoveryRequest request, HttpContext http, TenantRegistry registry, ICurrentPrincipal actor, CancellationToken cancellationToken)
    {
        // The operator by name, as the control plane's Identity knows them; their id when no directory is hosted.
        var directory = http.RequestServices.GetService<IPrincipals>();
        var found = directory is null ? null : await directory.FindAsync([actor.Id], cancellationToken);
        var operatorName = found is not null && found.TryGetValue(actor.Id, out var summary) ? summary.DisplayName : actor.Id.ToString("D");
        var result = await registry.RequestAdministratorRecoveryAsync(slug, request.LoginId, request.Reason, operatorName, cancellationToken, request.ResetFactors ?? false);
        return ApiResults.From(result, http, job => TypedResults.Accepted($"{ApiRouting.Prefix}/tenants/{slug}/provisioning", ProvisioningResponse.From(job)));
    }

    private static async Task<IResult> ProvisioningAsync(string slug, HttpContext http, IProvisioningJobs jobs, IJobHandovers handovers, ICurrentPrincipal actor, TenantRegistry registry, CancellationToken cancellationToken)
    {
        var tenant = await registry.FindAsync(slug, cancellationToken);
        if (tenant is null)
        {
            return ApiResults.Problem(Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'."), http);
        }

        var job = await jobs.LatestAsync(slug, cancellationToken);
        if (job is null)
        {
            return ApiResults.Problem(Error.NotFound("fleet.job.not_found", $"Tenant '{slug}' has no provisioning job yet."), http);
        }

        // The link the job handed over, once, to the operator who asked (ADR-0046); the answer is no-store, so no cache keeps it.
        var answer = job.Status == ProvisioningStatus.Completed ? await handovers.TakeAsync(job.Id, actor.Id, cancellationToken) : new JobHandoverAnswer(HandoverState.None);
        HandoverResponse? handover = answer.State switch
        {
            HandoverState.Given when answer.Item is { } item => new HandoverResponse("given", item.Kind, item.Subject, $"https://{tenant.Hostnames[0]}{item.LinkPath}", item.ExpiresAt),
            HandoverState.Taken => new HandoverResponse("taken", HandedOverAt: job.HandedOverAt),
            HandoverState.Expired => new HandoverResponse("expired"),
            HandoverState.NotYours => new HandoverResponse("not_yours"),
            _ => null,
        };
        if (answer.State == HandoverState.Given)
        {
            http.Response.Headers.CacheControl = "no-store";
        }

        return TypedResults.Ok(ProvisioningResponse.From(job, handover));
    }
}
