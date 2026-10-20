using FluentValidation;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Iron.Kernel.Authorization.Api;

/// <summary>A delegation to create. Without a delegator, the person asking delegates their own duties.</summary>
public sealed record CreateDelegationRequest(Guid? DelegatorId, Guid DelegateId, IReadOnlyList<string> Duties, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Reason);

public sealed class CreateDelegationRequestValidator : AbstractValidator<CreateDelegationRequest>
{
    public CreateDelegationRequestValidator()
    {
        RuleFor(r => r.DelegateId).NotEmpty().WithMessage("Choose who takes the duties.");
        RuleFor(r => r.Duties).NotEmpty().WithMessage("Choose at least one duty.");
        RuleFor(r => r.Reason).NotEmpty().WithMessage("Say why, in words the delegate and the auditors will read.").MaximumLength(Delegation.ReasonMaxLength);
    }
}

public sealed record EndDelegationRequest(string? Reason);

/// <summary>A duty a person may delegate, as the dialog offers it.</summary>
public sealed record DutyResponse(string Code, string Module, string Name, string Description, IReadOnlyList<string> Permissions);

/// <summary>
/// Delegations of duties (TF-037, ADR-0037). Any signed-in person reads and creates their own and ends one they are part
/// of; delegating for someone else, and seeing others' delegations, needs <c>authz.delegations.manage</c> where that
/// person's home unit is. The permission depends on whose delegation it is, so the endpoints are for anyone signed in
/// and the service checks the place.
/// </summary>
internal static class DelegationsEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/duties", Duties).RequireSignedIn().WithTags("delegations").WithName("ListDuties").WithSummary("The duties a person may delegate, with the permissions each covers.");

        var delegations = endpoints.MapApiGroup("delegations");
        delegations.MapGet("/", ListAsync).RequireSignedIn().WithName("ListDelegations").WithSummary("Your delegations (view=mine), or those of the people whose home unit you hold authz.delegations.manage at (view=managed).");
        delegations.MapPost("/", CreateAsync).RequireSignedIn().Validate<CreateDelegationRequest>().WithName("CreateDelegation").WithSummary("Delegate duties for a period with a reason: your own, or someone else's with authz.delegations.manage at their home unit.");
        delegations.MapPost("/{id:guid}/end", EndAsync).RequireSignedIn().WithName("EndDelegation").WithSummary("End a delegation now: its delegator, its delegate, or an administrator of the delegator.");
    }

    private static IResult Duties(DutyDeclarations duties) =>
        TypedResults.Ok(new { data = duties.All.Select(d => new DutyResponse(d.Duty.Code, d.Module, d.Duty.Name, d.Duty.Description, [.. d.Duty.Permissions.Select(p => p.Name)])).ToList() });

    private static async Task<IResult> ListAsync(string? view, HttpContext http, DelegationService delegations, CancellationToken cancellationToken)
    {
        if (string.Equals(view, "managed", StringComparison.Ordinal))
        {
            var managed = await delegations.ManagedAsync(cancellationToken);
            return managed is null
                ? ApiResults.Problem(Error.Forbidden(PermissionErrors.PermissionDenied, "Others' delegations need authz.delegations.manage. Your own are under view=mine."), http)
                : TypedResults.Ok(new { data = managed });
        }

        return TypedResults.Ok(new { data = await delegations.MineAsync(cancellationToken) });
    }

    private static async Task<IResult> CreateAsync(CreateDelegationRequest request, HttpContext http, DelegationService delegations, CancellationToken cancellationToken) =>
        ApiResults.From(
            await delegations.CreateAsync(new DelegationInput(request.DelegatorId, request.DelegateId, request.Duties, request.StartsAt, request.EndsAt, request.Reason), PermissionFilter.Operation(http), cancellationToken),
            http,
            view => TypedResults.Created($"/api/v1/delegations/{view.Id}", view));

    private static async Task<IResult> EndAsync(Guid id, EndDelegationRequest? request, HttpContext http, DelegationService delegations, CancellationToken cancellationToken) =>
        ApiResults.From(await delegations.EndAsync(id, request?.Reason, PermissionFilter.Operation(http), cancellationToken), http, view => TypedResults.Ok(view));
}
