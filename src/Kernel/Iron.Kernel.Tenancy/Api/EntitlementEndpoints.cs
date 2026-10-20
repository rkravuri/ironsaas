using System.Text.Json;
using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Tenancy.Api;

public sealed record PlanResponse(Guid Id, string Code, string Name, int GraceDays, IReadOnlyList<PlanModule> Modules, IReadOnlyList<string> Features, DateTimeOffset UpdatedAt, int Version)
{
    public static PlanResponse From(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new PlanResponse(plan.Id, plan.Code, plan.Name, plan.GraceDays, plan.Modules, plan.Features, plan.UpdatedAt, plan.Version);
    }
}

public sealed record SavePlanRequest(string Name, int GraceDays, IReadOnlyList<PlanModuleInput>? Modules, IReadOnlyList<string>? Features);

public sealed class SavePlanRequestValidator : AbstractValidator<SavePlanRequest>
{
    public SavePlanRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(Plan.NameMaxLength);
        RuleFor(r => r.GraceDays).InclusiveBetween(0, 365);
        RuleForEach(r => r.Modules).ChildRules(module => module.RuleFor(m => m.Code).NotEmpty());
        RuleForEach(r => r.Features).NotEmpty();
    }
}

public sealed record ApplyPlanRequest(string Plan, bool DryRun, DateOnly? StartsOn, DateOnly? EndsOn);

public sealed class ApplyPlanRequestValidator : AbstractValidator<ApplyPlanRequest>
{
    public ApplyPlanRequestValidator()
    {
        RuleFor(r => r.Plan).NotEmpty().MaximumLength(Plan.CodeMaxLength);
    }
}

public sealed record EntitlementResponse(string Kind, string Code, EntitlementStatus Status, SubscriptionType? SubscriptionType, int? UserLimit, DateOnly StartsOn, DateOnly? EndsOn);

public sealed record TenantEntitlementsResponse(string Tenant, string Plan, int GraceDays, int Revision, int PublishedRevision, IReadOnlyList<EntitlementResponse> Entitlements);

public sealed record EntitlementChangeResponse(Guid Id, int Revision, string Kind, string Code, JsonElement? Before, JsonElement? After, string Reason, Guid OperatorId, string? OperatorName, DateTimeOffset ChangedAt);

/// <summary>
/// Plans and tenants' entitlements in the control plane (TF-040, TF-042, TF-045): plans saved against the catalogue, a
/// plan applied with a dry run first, the current entitlements, and the history, which has no route that writes.
/// </summary>
internal static class EntitlementEndpoints
{
    public static readonly ListFields<Plan> PlanFields = new ListFields<Plan>()
        .Add("id", p => p.Id, FieldUse.Select | FieldUse.Filter)
        .Add("code", p => p.Code)
        .Add("name", p => p.Name)
        .Add("grace_days", p => p.GraceDays, FieldUse.Select | FieldUse.Sort)
        .Add("updated_at", p => p.UpdatedAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("code");

    /// <summary>Listed in order (TF-045 AC2): oldest first unless the query sorts otherwise.</summary>
    public static readonly ListFields<EntitlementChange> HistoryFields = new ListFields<EntitlementChange>()
        .Add("id", c => c.Id, FieldUse.Select | FieldUse.Filter)
        .Add("revision", c => c.Revision, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("kind", c => c.Kind, FieldUse.Select | FieldUse.Filter)
        .Add("code", c => c.Code)
        .Add("operator_id", c => c.CreatedBy, FieldUse.Select | FieldUse.Filter)
        .Add("changed_at", c => c.CreatedAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("changed_at");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var plans = endpoints.MapApiGroup("plans");
        plans.MapGet("/", ListPlansAsync).RequirePermission(TenancyPermissions.EntitlementsRead).WithName("ListPlans").WithSummary("The plans: licensed modules, features and grace days.");
        plans.MapGet("/{code}", GetPlanAsync).RequirePermission(TenancyPermissions.EntitlementsRead).WithName("GetPlan").WithSummary("One plan.");
        plans.MapPut("/{code}", SavePlanAsync).RequirePermission(TenancyPermissions.EntitlementsManage).Validate<SavePlanRequest>().WithName("SavePlan").WithSummary("Create a plan or replace what it holds; refused for a module or feature this build cannot license.");

        var tenants = endpoints.MapApiGroup("tenants");
        tenants.MapGet("/{slug}/entitlements", CurrentAsync).RequirePermission(TenancyPermissions.EntitlementsRead).WithName("GetTenantEntitlements").WithSummary("The tenant's plan, grace days, revision and entitlements.");
        tenants.MapPost("/{slug}/plan", ApplyAsync).RequirePermission(TenancyPermissions.EntitlementsManage).Validate<ApplyPlanRequest>().WithName("ApplyPlan").WithSummary("Apply a plan to the tenant in one transaction; with dry_run, only the differences and warnings.");
        tenants.MapGet("/{slug}/entitlements/history", HistoryAsync).RequirePermission(TenancyPermissions.EntitlementsRead).WithName("GetEntitlementHistory").WithSummary("Every change of the tenant's entitlements in order, with operator, before and after.");
    }

    private static async Task<IResult> ListPlansAsync(HttpContext http, TenancyDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, PlanFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.Plans.AsNoTracking().ApplyListQuery(query, PlanFields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(PlanResponse.From), http, query);
    }

    private static async Task<IResult> GetPlanAsync(string code, HttpContext http, PlanService plans, CancellationToken cancellationToken)
    {
        var plan = await plans.FindAsync(code, cancellationToken);
        return plan is null ? ApiResults.Problem(Error.NotFound(TenancyErrors.PlanNotFound, $"There is no plan '{code}'."), http) : TypedResults.Ok(PlanResponse.From(plan));
    }

    private static async Task<IResult> SavePlanAsync(string code, SavePlanRequest request, HttpContext http, PlanService plans, CancellationToken cancellationToken)
    {
        var result = await plans.SaveAsync(code, new PlanInput(request.Name, request.GraceDays, request.Modules ?? [], request.Features ?? []), cancellationToken);
        return ApiResults.From(result, http, plan => TypedResults.Ok(PlanResponse.From(plan)));
    }

    private static async Task<IResult> CurrentAsync(string slug, HttpContext http, TenancyDbContext context, EntitlementService entitlements, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken);
        if (tenant is null)
        {
            return ApiResults.Problem(Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'."), http);
        }

        var rows = await entitlements.RowsAsync(tenant.Id, asTracking: false, cancellationToken);
        var items = rows.Select(r => new EntitlementResponse(EntitlementService.KindName(r.Kind), r.Code, r.Status, r.SubscriptionType, r.UserLimit, r.StartsOn, r.EndsOn)).ToList();
        return TypedResults.Ok(new TenantEntitlementsResponse(tenant.Slug, tenant.Plan, tenant.GraceDays, tenant.EntitlementsRevision, tenant.PublishedRevision, items));
    }

    private static async Task<IResult> ApplyAsync(string slug, ApplyPlanRequest request, HttpContext http, EntitlementService entitlements, ITenantEntitlements publisher, ITenantFlags flags, CancellationToken cancellationToken)
    {
        var application = new PlanApplication(request.Plan, request.StartsOn, request.EndsOn, $"Plan {request.Plan.Trim().ToLowerInvariant()} applied");
        if (request.DryRun)
        {
            return ApiResults.From(await entitlements.PreviewAsync(slug, application, cancellationToken), http, preview => TypedResults.Ok(preview));
        }

        var applied = await entitlements.ApplyAsync(slug, application, cancellationToken);
        if (applied.IsSuccess && applied.Value.ChangesAnything)
        {
            // After the commit (ADR-0034): a store that cannot take it now gets it from the fleet's reconcile. The flags
            // follow, since their attribute rules read the plan (ADR-0035).
            await publisher.PublishAsync(slug, cancellationToken);
            await flags.PublishAsync(slug, cancellationToken);
        }

        return ApiResults.From(applied, http, preview => TypedResults.Ok(preview));
    }

    private static async Task<IResult> HistoryAsync(string slug, HttpContext http, TenancyDbContext context, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken);
        if (tenant is null)
        {
            return ApiResults.Problem(Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'."), http);
        }

        var parsed = ListQueryParser.Parse(http.Request.Query, HistoryFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.EntitlementHistory.AsNoTracking().Where(c => c.TenantId == tenant.Id).ApplyListQuery(query, HistoryFields).ToPageAsync(query.Page, cancellationToken);
        var principals = http.RequestServices.GetService<IPrincipals>();
        // Identity answers names in the control plane; a host without it shows the operator's id alone.
        IReadOnlyDictionary<Guid, PrincipalSummary> names = principals is null
            ? new Dictionary<Guid, PrincipalSummary>()
            : await principals.FindAsync([.. page.Items.Select(c => c.CreatedBy).Distinct()], cancellationToken);
        return ApiResults.List(page.Map(change => new EntitlementChangeResponse(change.Id, change.Revision, change.Kind, change.Code, Parse(change.Before), Parse(change.After), change.Reason, change.CreatedBy, names.GetValueOrDefault(change.CreatedBy)?.DisplayName, change.CreatedAt)), http, query);
    }

    private static JsonElement? Parse(string? json) => json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);
}
