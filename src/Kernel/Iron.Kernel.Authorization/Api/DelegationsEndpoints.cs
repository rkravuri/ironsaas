using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data.Lists;
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
    /// <summary>The administrators' list: filter by either person, sort by the period or when it was made.</summary>
    public static readonly ListFields<Delegation> Fields = new ListFields<Delegation>()
        .Add("id", d => d.Id, FieldUse.Select | FieldUse.Filter)
        .Add("delegator_id", d => d.DelegatorId, FieldUse.Select | FieldUse.Filter)
        .Add("delegate_id", d => d.DelegateId, FieldUse.Select | FieldUse.Filter)
        .Add("starts_at", d => d.StartsAt)
        .Add("ends_at", d => d.EndsAt)
        .Add("created_at", d => d.CreatedAt)
        .SortByDefault("starts_at", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/duties", Duties).RequireSignedIn().WithTags("delegations").WithName("ListDuties").WithSummary("The duties a person may delegate, with the permissions each covers.");

        var delegations = endpoints.MapApiGroup("delegations");
        delegations.MapGet("/", ListAsync).RequireSignedIn().WithName("ListDelegations").WithSummary("The delegations of the people whose home unit you hold authz.delegations.manage at; yours are under /mine.");
        delegations.MapGet("/mine", MineAsync).RequireSignedIn().WithName("MyDelegations").WithSummary("The delegations you give and hold, newest first; no permission needed, since they are yours.");
        delegations.MapPost("/", CreateAsync).RequireSignedIn().Validate<CreateDelegationRequest>().WithName("CreateDelegation").WithSummary("Delegate duties for a period with a reason: your own, or someone else's with authz.delegations.manage at their home unit.");
        delegations.MapPost("/{id:guid}/end", EndAsync).RequireSignedIn().WithName("EndDelegation").WithSummary("End a delegation now: its delegator, its delegate, or an administrator of the delegator.");
    }

    private static IResult Duties(DutyDeclarations duties) =>
        TypedResults.Ok(new { data = duties.All.Select(d => new DutyResponse(d.Duty.Code, d.Module, d.Duty.Name, d.Duty.Description, [.. d.Duty.Permissions.Select(p => p.Name)])).ToList() });

    private static async Task<IResult> ListAsync(HttpContext http, DelegationService delegations, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var managed = await delegations.ManagedAsync(cancellationToken);
        if (managed is null)
        {
            return ApiResults.Problem(Error.Forbidden(PermissionErrors.PermissionDenied, "Others' delegations need authz.delegations.manage. Yours are under /api/v1/delegations/mine."), http);
        }

        var query = parsed.Value;
        var page = await managed.ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        var views = (await delegations.DescribeAsync(page.Items, cancellationToken)).ToDictionary(v => v.Id);
        return ApiResults.List(page.Map(d => views[d.Id]), http, query);
    }

    /// <summary>A person's own delegations: not a paged list, because a person gives and holds a handful.</summary>
    private static async Task<IResult> MineAsync(DelegationService delegations, CancellationToken cancellationToken) =>
        TypedResults.Ok(new { data = await delegations.MineAsync(cancellationToken) });

    private static async Task<IResult> CreateAsync(CreateDelegationRequest request, HttpContext http, DelegationService delegations, CancellationToken cancellationToken) =>
        ApiResults.From(
            await delegations.CreateAsync(new DelegationInput(request.DelegatorId, request.DelegateId, request.Duties, request.StartsAt, request.EndsAt, request.Reason), PermissionFilter.Operation(http), cancellationToken),
            http,
            view => TypedResults.Created($"/api/v1/delegations/{view.Id}", view));

    private static async Task<IResult> EndAsync(Guid id, EndDelegationRequest? request, HttpContext http, DelegationService delegations, CancellationToken cancellationToken) =>
        ApiResults.From(await delegations.EndAsync(id, request?.Reason, PermissionFilter.Operation(http), cancellationToken), http, view => TypedResults.Ok(view));
}
