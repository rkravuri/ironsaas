using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data.Lists;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Authorization.Api;

public sealed record StartReviewRequest(string Name, DateOnly DueOn, IReadOnlyList<ReviewUnitInput> Units);

public sealed class StartReviewRequestValidator : AbstractValidator<StartReviewRequest>
{
    public StartReviewRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().WithMessage("Name the campaign, so reviewers know what they are asked.").MaximumLength(AccessReview.NameMaxLength);
        RuleFor(r => r.Units).NotEmpty().WithMessage("Choose the units to review.");
        RuleForEach(r => r.Units).ChildRules(unit => unit.RuleFor(u => u.UnitId).NotEmpty());
    }
}

/// <summary>A decision, with the signer's credentials when the tenant's policy has decisions signed (ADR-0051).</summary>
public sealed record DecideItemRequest(ReviewDecision Decision, string? Comment, DecisionSignatureRequest? Signature = null);

/// <summary>The credentials of a decision's signature, as the signing dialog asks for them.</summary>
public sealed record DecisionSignatureRequest(string? LoginId, string? Password, string? Code, string? RecoveryCode, System.Text.Json.JsonElement? Passkey)
{
    public SigningCredentials ToCredentials() => new(LoginId, Password, Code, RecoveryCode, Passkey);
}

public sealed class DecideItemRequestValidator : AbstractValidator<DecideItemRequest>
{
    public DecideItemRequestValidator()
    {
        RuleFor(r => r.Decision).IsInEnum();
        RuleFor(r => r.Comment).MaximumLength(AccessReviewDecision.CommentMaxLength);
    }
}

public sealed record ConfirmItemsRequest(IReadOnlyList<Guid> ItemIds, string? Comment);

public sealed class ConfirmItemsRequestValidator : AbstractValidator<ConfirmItemsRequest>
{
    public ConfirmItemsRequestValidator()
    {
        RuleFor(r => r.ItemIds).NotEmpty().WithMessage("Choose the items to confirm.")
            .Must(ids => ids.Count <= ReviewService.ConfirmManyLimit).WithMessage($"Confirm at most {ReviewService.ConfirmManyLimit} items at once: a page of the list.");
        RuleFor(r => r.Comment).MaximumLength(AccessReviewDecision.CommentMaxLength);
    }
}

/// <summary>A campaign in the list, with the progress of what the reader may see.</summary>
public sealed record ReviewSummaryResponse(Guid Id, string Name, DateOnly DueOn, ReviewStatus Status, bool Overdue, DateTimeOffset StartedAt, ReviewProgress Progress);

/// <summary>
/// Access reviews (TF-038, ADR-0037). Starting and closing need <c>authz.reviews.manage</c> tenant-wide. A reviewer holds
/// <c>authz.reviews.decide</c> at an item's unit: the item list keeps their units' items, and a decision is checked at
/// the item's unit. An item nobody but its own person may review goes to the campaign's owner, who may not hold the
/// permission there, and nobody decides their own access. So the campaigns, their items and the decisions are for anyone
/// signed in, and the service decides who may, refusing anyone else with an audited denial.
/// </summary>
internal static class AccessReviewsEndpoints
{
    public static readonly ListFields<AccessReview> Fields = new ListFields<AccessReview>()
        .Add("id", r => r.Id, FieldUse.Select | FieldUse.Filter)
        .Add("name", r => r.Name)
        .Add("status", r => r.Status)
        .Add("due_on", r => r.DueOn)
        .Add("created_at", r => r.CreatedAt)
        .SortByDefault("created_at", SortDirection.Descending);

    public static readonly ListFields<AccessReviewItem> ItemFields = new ListFields<AccessReviewItem>()
        .Add("id", i => i.Id, FieldUse.Select | FieldUse.Filter)
        .Add("principal_id", i => i.PrincipalId, FieldUse.Select | FieldUse.Filter)
        .Add("principal_name", i => i.PrincipalName)
        .Add("role_code", i => i.RoleCode)
        .Add("role_name", i => i.RoleName)
        .Add("unit_id", i => i.UnitId, FieldUse.Select | FieldUse.Filter)
        .Add("unit_name", i => i.UnitName)
        .SortByDefault("unit_name");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var reviews = endpoints.MapApiGroup("access-reviews");
        reviews.MapGet("/", ListAsync).RequireSignedIn().Produces<ListResponse<ReviewSummaryResponse>>(StatusCodes.Status200OK).WithName("ListAccessReviews").WithSummary("The campaigns you manage (authz.reviews.manage) or review (authz.reviews.decide at one of their units), with progress.");
        reviews.MapPost("/", StartAsync).RequirePermission(AuthorizationPermissions.ReviewsManage).Validate<StartReviewRequest>().Produces<ReviewView>(StatusCodes.Status201Created).WithName("StartAccessReview").WithSummary("Start a campaign over units, with or without their children: the assignments there are taken as its items now.");
        reviews.MapGet("/{id:guid}", GetAsync).RequireSignedIn().Produces<ReviewView>(StatusCodes.Status200OK).WithName("GetAccessReview").WithSummary("One campaign with the progress of what you may see, per unit, and its escalations.");
        reviews.MapPost("/{id:guid}/close", CloseAsync).RequirePermission(AuthorizationPermissions.ReviewsManage).Produces<ReviewView>(StatusCodes.Status200OK).WithName("CloseAccessReview").WithSummary("Close a campaign: no more decisions.");
        reviews.MapGet("/{id:guid}/items", ItemsAsync).RequireSignedIn().Produces<ListResponse<ReviewItemView>>(StatusCodes.Status200OK).WithName("ListAccessReviewItems").WithSummary("The items you decide: at the units where you hold authz.reviews.decide, and as the campaign's owner those nobody else may review; state=open or decided narrows them.");
        reviews.MapPost("/{id:guid}/items/{itemId:guid}/decision", DecideAsync)
            .RequireSignedIn()
            .Validate<DecideItemRequest>()
            .Produces<ReviewItemView>(StatusCodes.Status200OK)
            .WithName("DecideAccessReviewItem")
            .WithSummary("Confirm an assignment, or revoke it, which removes it at once; the last tenant administrator stays, and nobody decides their own access.");
        reviews.MapPost("/{id:guid}/confirm", ConfirmAsync).RequireSignedIn().Validate<ConfirmItemsRequest>().Produces<ConfirmManyView>(StatusCodes.Status200OK).WithName("ConfirmAccessReviewItems").WithSummary("Confirm a page of items at once; items decided meanwhile are left as they are, and none may be about your own access.");
    }

    private static async Task<IResult> ListAsync(HttpContext http, ReviewService reviews, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var visible = await reviews.VisibleAsync(cancellationToken);
        if (visible is null)
        {
            return ApiResults.Problem(await reviews.RefusalAsync(PermissionFilter.Operation(http), cancellationToken), http);
        }

        var query = parsed.Value;
        var page = await visible.ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        var progress = await reviews.ProgressAsync(page.Items, cancellationToken);
        return ApiResults.List(page.Map(r => new ReviewSummaryResponse(r.Id, r.Name, r.DueOn, r.Status, progress[r.Id].Overdue > 0, r.CreatedAt, progress[r.Id])), http, query);
    }

    private static async Task<IResult> StartAsync(StartReviewRequest request, HttpContext http, ReviewService reviews, CancellationToken cancellationToken) =>
        ApiResults.From(
            await reviews.StartAsync(new StartReviewInput(request.Name, request.DueOn, request.Units), cancellationToken),
            http,
            view => TypedResults.Created($"{ApiRouting.Prefix}/access-reviews/{view.Id}", view));

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, ReviewService reviews, CancellationToken cancellationToken)
    {
        if (await reviews.VisibleAsync(cancellationToken) is null)
        {
            return ApiResults.Problem(await reviews.RefusalAsync(PermissionFilter.Operation(http), cancellationToken), http);
        }

        return ApiResults.From(await reviews.DescribeAsync(id, cancellationToken), http, view => TypedResults.Ok(view));
    }

    private static async Task<IResult> CloseAsync(Guid id, HttpContext http, ReviewService reviews, CancellationToken cancellationToken) =>
        ApiResults.From(await reviews.CloseAsync(id, cancellationToken), http, view => TypedResults.Ok(view));

    private static async Task<IResult> ItemsAsync(Guid id, string? state, HttpContext http, ReviewService reviews, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, ItemFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var narrowed = state switch
        {
            "open" => ItemState.Open,
            "decided" => ItemState.Decided,
            _ => ItemState.All,
        };
        var query = parsed.Value;
        var items = await reviews.ItemsAsync(id, narrowed, PermissionFilter.Operation(http), cancellationToken);
        if (items.IsFailure)
        {
            return ApiResults.Problem(items.Error, http);
        }

        var page = await items.Value.ApplyListQuery(query, ItemFields).ToPageAsync(query.Page, cancellationToken);
        var views = (await reviews.DescribeItemsAsync(page.Items, cancellationToken)).ToDictionary(v => v.Id);
        return ApiResults.List(page.Map(i => views[i.Id]), http, query);
    }

    private static async Task<IResult> DecideAsync(Guid id, Guid itemId, DecideItemRequest request, HttpContext http, ReviewService reviews, CancellationToken cancellationToken) =>
        ApiResults.From(await reviews.DecideAsync(id, itemId, request.Decision, request.Comment, request.Signature?.ToCredentials(), PermissionFilter.Operation(http), cancellationToken), http, view => TypedResults.Ok(view));

    private static async Task<IResult> ConfirmAsync(Guid id, ConfirmItemsRequest request, HttpContext http, ReviewService reviews, CancellationToken cancellationToken) =>
        ApiResults.From(await reviews.ConfirmManyAsync(id, request.ItemIds, request.Comment, PermissionFilter.Operation(http), cancellationToken), http, view => TypedResults.Ok(view));
}
