using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Problems;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Data.Visibility;
using Iron.Kernel.Visibility.Contracts;
using Iron.Kernel.Visibility.Data;
using Iron.Kernel.Visibility.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Visibility.Api;

/// <summary>A tag as the console lists it: its code, name, description, status, and how many people and service accounts hold it.</summary>
public sealed record TagResponse(Guid Id, string Code, string Name, string? Description, string Status, int Holders, DateTimeOffset UpdatedAt, int Version);

/// <summary>A tag as a person's or a record's tags name it.</summary>
public sealed record TagSummaryResponse(string Code, string Name, bool Active);

/// <summary>One holder of a tag: who, of which kind, and their home unit.</summary>
public sealed record TagHolderResponse(Guid PrincipalId, string Name, PrincipalKind Type, Guid? HomeUnitId, DateTimeOffset Since);

/// <summary>
/// A person's tags on their record: the tags, whether the reader may change them, and the tags the reader may give,
/// under the escalation rule (the active tags the reader holds, or every active tag when they hold none).
/// </summary>
public sealed record HolderTagsResponse(Guid PrincipalId, IReadOnlyList<TagSummaryResponse> Tags, bool MayChange, IReadOnlyList<TagSummaryResponse> Choices);

/// <summary>A record's tags on its page: the tags, whether the reader may change them, and the tags they may put on it (rule 5).</summary>
public sealed record RecordTagsResponse(string RecordType, Guid RecordId, IReadOnlyList<TagSummaryResponse> Tags, bool MayChange, IReadOnlyList<TagSummaryResponse> Choices);

public sealed record CreateTagRequest(string Code, string Name, string? Description);

public sealed record ChangeTagRequest(string Name, string? Description);

/// <summary>The tags to hold, or to carry: the full list, which replaces the one there.</summary>
public sealed record SetTagsRequest(IReadOnlyList<string>? Codes);

public sealed class CreateTagRequestValidator : AbstractValidator<CreateTagRequest>
{
    public CreateTagRequestValidator()
    {
        RuleFor(r => r.Code).NotEmpty().MaximumLength(VisibilityTag.CodeMaxLength);
        RuleFor(r => r.Name).NotEmpty().MaximumLength(VisibilityTag.NameMaxLength);
        RuleFor(r => r.Description).MaximumLength(VisibilityTag.DescriptionMaxLength);
    }
}

public sealed class ChangeTagRequestValidator : AbstractValidator<ChangeTagRequest>
{
    public ChangeTagRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(VisibilityTag.NameMaxLength);
        RuleFor(r => r.Description).MaximumLength(VisibilityTag.DescriptionMaxLength);
    }
}

public sealed class SetTagsRequestValidator : AbstractValidator<SetTagsRequest>
{
    /// <summary>More tags than anyone could read on a record: the list is a choice, not a dump.</summary>
    public const int MaxTags = 50;

    public SetTagsRequestValidator()
    {
        RuleFor(r => r.Codes).MaximumCount(MaxTags).WithMessage($"List at most {MaxTags} tags.");
        RuleForEach(r => r.Codes).NotEmpty().MaximumLength(VisibilityTag.CodeMaxLength);
    }
}

/// <summary>
/// Visibility tags (TF-241, ADR-0057): the tenant's tags, read with <c>visibility.tags.read</c> and written with
/// <c>visibility.tags.manage</c>; who holds a tag; a person's tags, read with <c>visibility.tags.read</c> and changed with
/// <c>visibility.assignments.manage</c> at their home unit; and any business record's tags, which its policy guards
/// (Visibility maps them for the data library, which knows records but has no endpoints).
/// </summary>
internal static class VisibilityEndpoints
{
    public static readonly ListFields<VisibilityTag> TagFields = new ListFields<VisibilityTag>()
        .Add("id", t => t.Id, FieldUse.Select | FieldUse.Filter)
        .Add("code", t => t.Code)
        .Add("name", t => t.Name)
        .Add("status", t => t.Status, FieldUse.Select | FieldUse.Filter)
        .Add("updated_at", t => t.UpdatedAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("code");

    public static readonly ListFields<TagHolding> HolderFields = new ListFields<TagHolding>()
        .Add("principal_id", h => h.PrincipalId, FieldUse.Select | FieldUse.Filter)
        .Add("created_at", h => h.CreatedAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("created_at");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var tags = endpoints.MapApiGroup("visibility-tags");
        tags.MapGet("/", ListAsync).RequirePermission(VisibilityPermissions.TagsRead).ScopedByRow().Produces<ListResponse<TagResponse>>(StatusCodes.Status200OK).WithName("ListVisibilityTags").WithSummary("The visibility tags of this tenant, with how many people hold each.");
        tags.MapPost("/", CreateAsync).RequirePermission(VisibilityPermissions.TagsManage).Validate<CreateTagRequest>().Produces<TagResponse>(StatusCodes.Status201Created).WithName("CreateVisibilityTag").WithSummary("Define a visibility tag: a code it keeps for good, a name and a description.");
        tags.MapPut("/{id:guid}", ChangeAsync).RequirePermission(VisibilityPermissions.TagsManage).Validate<ChangeTagRequest>().Produces<TagResponse>(StatusCodes.Status200OK).WithName("ChangeVisibilityTag").WithSummary("Rename a visibility tag or change its description; its code stays.");
        tags.MapPost("/{id:guid}/retire", RetireAsync).RequirePermission(VisibilityPermissions.TagsManage).Produces<TagResponse>(StatusCodes.Status200OK).WithName("RetireVisibilityTag").WithSummary("Retire a visibility tag: it stays on the records and people that carry it, and is put on nothing new.");
        tags.MapPost("/{id:guid}/reactivate", ReactivateAsync).RequirePermission(VisibilityPermissions.TagsManage).Produces<TagResponse>(StatusCodes.Status200OK).WithName("ReactivateVisibilityTag").WithSummary("Make a retired visibility tag active again.");
        tags.MapGet("/{id:guid}/holders", HoldersAsync).RequirePermission(VisibilityPermissions.TagsRead).ScopedByRow().Produces<ListResponse<TagHolderResponse>>(StatusCodes.Status200OK).WithName("ListVisibilityTagHolders").WithSummary("The people and service accounts who hold a visibility tag.");

        // The permission is checked at the principal's home unit, which the route does not carry (as Identity does, ADR-0031).
        var users = endpoints.MapApiGroup("users");
        users.MapGet("/{id:guid}/visibility-tags", HolderTagsAsync).RequirePermission(VisibilityPermissions.TagsRead).ScopedByRow().Produces<HolderTagsResponse>(StatusCodes.Status200OK).WithName("GetHolderVisibilityTags").WithSummary("The visibility tags a person or a service account holds, and those you may give them.");
        users.MapPut("/{id:guid}/visibility-tags", SetHolderTagsAsync).RequirePermission(VisibilityPermissions.AssignmentsManage).ScopedByRow().Validate<SetTagsRequest>().Produces<HolderTagsResponse>(StatusCodes.Status200OK).WithName("SetHolderVisibilityTags").WithSummary("Give a person or a service account exactly these visibility tags; you give and take away only tags you hold, unless you hold none.");

        var records = endpoints.MapApiGroup("record-tags");
        records.MapGet("/{recordType}/{id:guid}", RecordTagsAsync).RequireSignedIn().Produces<RecordTagsResponse>(StatusCodes.Status200OK).WithName("GetRecordVisibilityTags").WithSummary("The visibility tags a record carries, and those you may put on it.");
        records.MapPut("/{recordType}/{id:guid}", SetRecordTagsAsync).RequireSignedIn().Validate<SetTagsRequest>().Produces<RecordTagsResponse>(StatusCodes.Status200OK).WithName("SetRecordVisibilityTags").WithSummary("Give a record exactly these visibility tags; its policy decides who may, and you put on or take off only tags you hold, unless you hold none.");
    }

    private static async Task<IResult> ListAsync(HttpContext http, VisibilityDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, TagFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.Tags.AsNoTracking().ApplyListQuery(query, TagFields).ToPageAsync(query.Page, cancellationToken);
        var counts = await CountsAsync(context, [.. page.Items.Select(t => t.Id)], cancellationToken);
        return ApiResults.List(page.Map(t => Describe(t, counts.GetValueOrDefault(t.Id))), http, query);
    }

    private static async Task<IResult> CreateAsync(CreateTagRequest request, HttpContext http, TagService tags, CancellationToken cancellationToken)
    {
        var created = await tags.CreateAsync(request.Code, request.Name, request.Description, cancellationToken);
        return created.IsFailure ? ApiResults.Problem(created.Error, http) : TypedResults.Created($"{ApiRouting.Prefix}/visibility-tags/{created.Value.Id}", Describe(created.Value, 0));
    }

    private static async Task<IResult> ChangeAsync(Guid id, ChangeTagRequest request, HttpContext http, TagService tags, VisibilityDbContext context, CancellationToken cancellationToken) =>
        await AnswerAsync(await tags.ChangeAsync(id, request.Name, request.Description, cancellationToken), http, context, cancellationToken);

    private static async Task<IResult> RetireAsync(Guid id, HttpContext http, TagService tags, VisibilityDbContext context, CancellationToken cancellationToken) =>
        await AnswerAsync(await tags.RetireAsync(id, cancellationToken), http, context, cancellationToken);

    private static async Task<IResult> ReactivateAsync(Guid id, HttpContext http, TagService tags, VisibilityDbContext context, CancellationToken cancellationToken) =>
        await AnswerAsync(await tags.ReactivateAsync(id, cancellationToken), http, context, cancellationToken);

    private static async Task<IResult> HoldersAsync(Guid id, HttpContext http, VisibilityDbContext context, IPrincipals principals, CancellationToken cancellationToken)
    {
        if (!await context.Tags.AnyAsync(t => t.Id == id, cancellationToken))
        {
            return ApiResults.Problem(Error.NotFound(VisibilityErrors.TagNotFound, "There is no visibility tag with this id."), http);
        }

        var parsed = ListQueryParser.Parse(http.Request.Query, HolderFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.Holdings.AsNoTracking().Where(h => h.TagId == id).ApplyListQuery(query, HolderFields).ToPageAsync(query.Page, cancellationToken);
        var people = await principals.FindAsync([.. page.Items.Select(h => h.PrincipalId)], cancellationToken);
        return ApiResults.List(
            page.Map(h => people.TryGetValue(h.PrincipalId, out var person)
                ? new TagHolderResponse(h.PrincipalId, person.DisplayName, person.Type, person.HomeUnitId, h.CreatedAt)
                : new TagHolderResponse(h.PrincipalId, h.PrincipalId.ToString("D"), PrincipalKind.User, null, h.CreatedAt)),
            http,
            query);
    }

    private static async Task<IResult> HolderTagsAsync(Guid id, HttpContext http, IPrincipals principals, HoldingService holdings, CancellationToken cancellationToken)
    {
        var principal = await AtHomeUnitAsync(id, VisibilityPermissions.TagsRead, http, principals, cancellationToken);
        return principal.IsFailure ? Refused(principal.Error, http, VisibilityPermissions.TagsRead) : TypedResults.Ok(await HolderAsync(principal.Value, http, holdings, cancellationToken));
    }

    private static async Task<IResult> SetHolderTagsAsync(Guid id, SetTagsRequest request, HttpContext http, IPrincipals principals, HoldingService holdings, CancellationToken cancellationToken)
    {
        var principal = await AtHomeUnitAsync(id, VisibilityPermissions.AssignmentsManage, http, principals, cancellationToken);
        if (principal.IsFailure)
        {
            return Refused(principal.Error, http, VisibilityPermissions.AssignmentsManage);
        }

        var set = await holdings.SetAsync(principal.Value.Id, principal.Value.DisplayName, request.Codes ?? [], cancellationToken);
        return set.IsFailure ? ApiResults.Problem(set.Error, http) : TypedResults.Ok(await HolderAsync(principal.Value, http, holdings, cancellationToken));
    }

    private static async Task<IResult> RecordTagsAsync(string recordType, Guid id, HttpContext http, IRecordTags tags, CancellationToken cancellationToken)
    {
        var result = await tags.GetAsync(recordType, id, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok(Describe(result.Value));
    }

    private static async Task<IResult> SetRecordTagsAsync(string recordType, Guid id, SetTagsRequest request, HttpContext http, IRecordTags tags, CancellationToken cancellationToken)
    {
        var result = await tags.ChangeAsync(recordType, id, request.Codes ?? [], cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok(Describe(result.Value));
    }

    /// <summary>
    /// The principal the request names, when the reader holds the permission at their home unit (tenant-wide for one
    /// without a home unit); a principal of no one is not found, a refusal is audited like any other check.
    /// </summary>
    private static async Task<Result<PrincipalSummary>> AtHomeUnitAsync(Guid id, Permission permission, HttpContext http, IPrincipals principals, CancellationToken cancellationToken)
    {
        if (!(await principals.FindAsync([id], cancellationToken)).TryGetValue(id, out var principal))
        {
            return Error.NotFound(VisibilityErrors.PrincipalNotFound, "There is no person or service account with this id.");
        }

        var evaluator = http.RequestServices.GetRequiredService<IPermissionEvaluator>();
        var actor = http.RequestServices.GetRequiredService<ICurrentPrincipal>();
        var decision = await evaluator.EvaluateAsync(Check(actor.Id, permission, principal, http), cancellationToken);
        return decision.Allowed ? principal : Error.Forbidden(PermissionErrors.PermissionDenied, $"You do not hold {permission.Name} at {principal.DisplayName}'s unit.");
    }

    private static AccessRequest Check(Guid actorId, Permission permission, PrincipalSummary principal, HttpContext http) =>
        new(actorId, permission, PermissionFilter.Operation(http), http.Request.Path.Value ?? "/") { Scope = principal.HomeUnitId is { } unit ? AccessScope.Unit(unit) : AccessScope.Tenant };

    private static async Task<HolderTagsResponse> HolderAsync(PrincipalSummary principal, HttpContext http, HoldingService holdings, CancellationToken cancellationToken)
    {
        var actor = http.RequestServices.GetRequiredService<ICurrentPrincipal>();
        var evaluator = http.RequestServices.GetRequiredService<IPermissionEvaluator>();

        // Whether to offer the change, decided without auditing a refusal: the reader only looks.
        var mayChange = (await evaluator.ExplainAsync(Check(actor.Id, VisibilityPermissions.AssignmentsManage, principal, http), cancellationToken)).Allowed;
        var held = await holdings.TagsOfAsync(principal.Id, cancellationToken);
        var choices = mayChange ? await holdings.GivableAsync(cancellationToken) : [];
        return new HolderTagsResponse(principal.Id, [.. held.Select(Summary)], mayChange, [.. choices.Select(Summary)]);
    }

    private static IResult Refused(Error error, HttpContext http, Permission permission)
    {
        var problem = ProblemFactory.Create(error, http);
        if (error.Code == PermissionErrors.PermissionDenied)
        {
            problem.Extensions["permission"] = permission.Name;
        }

        return TypedResults.Problem(problem);
    }

    private static async Task<IResult> AnswerAsync(Result<VisibilityTag> result, HttpContext http, VisibilityDbContext context, CancellationToken cancellationToken)
    {
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error, http);
        }

        var counts = await CountsAsync(context, [result.Value.Id], cancellationToken);
        return TypedResults.Ok(Describe(result.Value, counts.GetValueOrDefault(result.Value.Id)));
    }

    private static async Task<Dictionary<Guid, int>> CountsAsync(VisibilityDbContext context, Guid[] tagIds, CancellationToken cancellationToken) =>
        await context.Holdings.AsNoTracking().Where(h => tagIds.Contains(h.TagId)).GroupBy(h => h.TagId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

    private static TagResponse Describe(VisibilityTag tag, int holders) =>
        new(tag.Id, tag.Code, tag.Name, tag.Description, tag.IsActive ? "active" : "retired", holders, tag.UpdatedAt, tag.Version);

    private static TagSummaryResponse Summary(VisibilityTag tag) => new(tag.Code, tag.Name, tag.IsActive);

    private static RecordTagsResponse Describe(RecordTagsView view) =>
        new(view.RecordType, view.RecordId, [.. view.Tags.Select(Summary)], view.MayChange, [.. view.Choices.Select(Summary)]);

    private static TagSummaryResponse Summary(VisibilityTagInfo tag) => new(tag.Code, tag.Name, tag.Active);
}
