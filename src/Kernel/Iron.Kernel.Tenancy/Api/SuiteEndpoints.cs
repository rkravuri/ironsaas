using FluentValidation;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Iron.Kernel.Tenancy.Api;

/// <summary>A suite to add to an active tenant, under one of its plans (its only plan when none is named), with the operator's reason.</summary>
public sealed record AddSuiteRequest(string Suite, string? Plan, DateOnly? StartsOn, DateOnly? EndsOn, string Reason);

public sealed class AddSuiteRequestValidator : AbstractValidator<AddSuiteRequest>
{
    public AddSuiteRequestValidator()
    {
        RuleFor(r => r.Suite).NotEmpty().MaximumLength(Plan.SuiteMaxLength);
        RuleFor(r => r.Plan).MaximumLength(Plan.CodeMaxLength);
        RuleFor(r => r.Reason).NotEmpty().WithMessage("Say why: the audit trail keeps it with your name.").MaximumLength(SuiteService.ReasonMaxLength);
    }
}

/// <summary>Why a suite is removed from a tenant.</summary>
public sealed record RemoveSuiteRequest(string Reason);

public sealed class RemoveSuiteRequestValidator : AbstractValidator<RemoveSuiteRequest>
{
    public RemoveSuiteRequestValidator()
    {
        RuleFor(r => r.Reason).NotEmpty().WithMessage("Say why: the audit trail keeps it with your name.").MaximumLength(SuiteService.ReasonMaxLength);
    }
}

/// <summary>What adding or removing a suite did: the entitlements it changed and the update job that brings the unit along.</summary>
public sealed record SuiteChangeResponse(string Suite, PlanPreview Entitlements, ProvisioningResponse Job);

/// <summary>
/// A tenant's suites in the control plane (TF-244, ADR-0059): which it has, under which plan, and the operator's add and
/// remove, each audited with a reason and carried out by the fleet's update job.
/// </summary>
internal static class SuiteEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var tenants = endpoints.MapApiGroup("tenants");
        tenants.MapGet("/{slug}/suites", ListAsync).RequirePermission(TenancyPermissions.TenantsRead).Produces<IReadOnlyList<TenantSuiteResponse>>(StatusCodes.Status200OK).WithName("ListTenantSuites").WithSummary("The suites the tenant has, with the plan it holds for each.");
        tenants.MapPost("/{slug}/suites", AddAsync).RequirePermission(TenancyPermissions.SuitesManage).Validate<AddSuiteRequest>().Produces<SuiteChangeResponse>(StatusCodes.Status202Accepted).WithName("AddTenantSuite").WithSummary("Add a suite to an active tenant with one of its plans and a reason; the fleet's update job migrates, seeds and restarts the unit with it.");
        tenants.MapPost("/{slug}/suites/{suite}/remove", RemoveAsync).RequirePermission(TenancyPermissions.SuitesManage).Validate<RemoveSuiteRequest>().Produces<SuiteChangeResponse>(StatusCodes.Status202Accepted).WithName("RemoveTenantSuite").WithSummary("Remove a suite from an active tenant with a reason; its modules are switched off and the unit restarts without it, and its data stays.");
    }

    public static TenantSuiteResponse ToResponse(TenantSuite suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        return new TenantSuiteResponse(suite.Suite, suite.Plan, suite.GraceDays, suite.CreatedAt);
    }

    private static async Task<IResult> ListAsync(string slug, HttpContext http, SuiteService suites, CancellationToken cancellationToken) =>
        ApiResults.From(await suites.ListAsync(slug, cancellationToken), http, list => TypedResults.Ok(list.Select(ToResponse).ToList()));

    private static async Task<IResult> AddAsync(string slug, AddSuiteRequest request, HttpContext http, SuiteService suites, CancellationToken cancellationToken)
    {
        var result = await suites.AddAsync(slug, new SuiteAddition(request.Suite, request.Plan, request.StartsOn, request.EndsOn, request.Reason), cancellationToken);
        return ApiResults.From(result, http, change => TypedResults.Accepted($"{ApiRouting.Prefix}/tenants/{slug}/provisioning", Response(change)));
    }

    private static async Task<IResult> RemoveAsync(string slug, string suite, RemoveSuiteRequest request, HttpContext http, SuiteService suites, CancellationToken cancellationToken)
    {
        var result = await suites.RemoveAsync(slug, suite, request.Reason, cancellationToken);
        return ApiResults.From(result, http, change => TypedResults.Accepted($"{ApiRouting.Prefix}/tenants/{slug}/provisioning", Response(change)));
    }

    private static SuiteChangeResponse Response(SuiteChange change) => new(change.Suite, change.Entitlements, ProvisioningResponse.From(change.Job));
}
