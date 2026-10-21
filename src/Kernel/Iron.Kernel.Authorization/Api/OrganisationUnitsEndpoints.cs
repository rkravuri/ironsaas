using FluentValidation;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Problems;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Iron.Kernel.Authorization.Api;

/// <summary>What a client sees of a unit. No business fields (ADR-0029).</summary>
public sealed record UnitResponse(
    Guid Id,
    string Name,
    string Code,
    string Kind,
    Guid? ParentId,
    int Depth,
    UnitStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version,
    Guid? MergedIntoId)
{
    public static UnitResponse From(OrganisationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return new UnitResponse(unit.Id, unit.Name, unit.Code, unit.Kind, unit.ParentId, unit.Depth, unit.Status, unit.CreatedAt, unit.UpdatedAt, unit.Version, unit.MergedIntoId);
    }
}

/// <summary>A unit's checklist from the modules (TF-223): ready when every item is done, or when no module asks anything.</summary>
public sealed record UnitReadinessResponse(Guid UnitId, bool Ready, IReadOnlyList<ReadinessItem> Items);

/// <summary>What one kind of thing a merge moves: for records the type's code and display name, at most twenty items in words, and the count of all of them.</summary>
public sealed record MergePartResponse(string Code, string Module, string? RecordType, string? RecordTypeName, int Count, IReadOnlyList<string> Items, IReadOnlyList<string> Conflicts)
{
    public const int ItemsShown = 20;

    public static MergePartResponse From(UnitMergePart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return new MergePartResponse(part.Code, part.Module, part.RecordType, part.RecordTypeName, part.Count, [.. part.Items.Take(ItemsShown)], part.Conflicts);
    }
}

/// <summary>A merge of one unit into another, before (the preview) or after (what moved).</summary>
public sealed record MergeResponse(UnitResponse From, UnitResponse Into, IReadOnlyList<MergePartResponse> Parts)
{
    public static MergeResponse Of(UnitMergeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new MergeResponse(UnitResponse.From(outcome.From), UnitResponse.From(outcome.Into), [.. outcome.Parts.Select(MergePartResponse.From)]);
    }
}

public sealed record MergeUnitRequest(Guid IntoId);

/// <summary>The whole tree, depth first from the root with siblings by name. Not paged: a tenant's tree is small (ADR-0030).</summary>
public sealed record UnitTreeResponse(IReadOnlyList<UnitResponse> Data);

public sealed record KindResponse(string Code, string Name, IReadOnlyList<string> ChildKinds);

public sealed record KindsResponse(IReadOnlyList<KindResponse> Data, string RootKind);

public sealed record CreateUnitRequest(string Name, string Code, string Kind);

public sealed record RenameUnitRequest(string Name);

public sealed record MoveUnitRequest(Guid ParentId);

public sealed class CreateUnitRequestValidator : AbstractValidator<CreateUnitRequest>
{
    public CreateUnitRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(OrganisationUnit.NameMaxLength);
        RuleFor(r => r.Code).NotEmpty().MaximumLength(OrganisationUnit.CodeMaxLength)
            .Must(code => OrganisationUnit.IsValidCode(OrganisationUnit.NormaliseCode(code))).WithErrorCode("invalid").WithMessage("Use lowercase letters, digits and hyphens.");
        RuleFor(r => r.Kind).NotEmpty().MaximumLength(OrganisationUnit.KindMaxLength);
    }
}

public sealed class RenameUnitRequestValidator : AbstractValidator<RenameUnitRequest>
{
    public RenameUnitRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(OrganisationUnit.NameMaxLength);
    }
}

public sealed class MergeUnitRequestValidator : AbstractValidator<MergeUnitRequest>
{
    public MergeUnitRequestValidator()
    {
        RuleFor(r => r.IntoId).NotEmpty();
    }
}

public sealed class MoveUnitRequestValidator : AbstractValidator<MoveUnitRequest>
{
    public MoveUnitRequestValidator()
    {
        RuleFor(r => r.ParentId).NotEmpty();
    }
}

/// <summary>
/// The organisation tree (TF-034, TF-223): read it whole, read one unit, create a unit under another, rename, move,
/// deactivate, reactivate and merge, and read a unit's readiness. A change needs <c>authz.units.manage</c> at the unit it
/// touches (ADR-0030): a role assigned to a unit with its children lets its holder shape that part of the tree; a move
/// also needs it at the new parent, and a merge at the unit merged into (ADR-0038).
/// </summary>
internal static class OrganisationUnitsEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var units = endpoints.MapApiGroup("organisation-units");
        units.MapGet("/", TreeAsync).RequirePermission(AuthorizationPermissions.UnitsRead).ScopedByRow().Produces<UnitTreeResponse>(StatusCodes.Status200OK).WithName("ListOrganisationUnits").WithSummary("The part of the organisation tree you may see, depth first.");
        units.MapGet("/{id:guid}", GetAsync).RequirePermission(AuthorizationPermissions.UnitsRead).ScopedToUnit().Produces<UnitResponse>(StatusCodes.Status200OK).WithName("GetOrganisationUnit").WithSummary("One organisation unit.");
        units.MapPost("/{id:guid}/children", CreateAsync).RequirePermission(AuthorizationPermissions.UnitsManage).ScopedToUnit().Validate<CreateUnitRequest>().Produces<UnitResponse>(StatusCodes.Status201Created).WithName("CreateOrganisationUnit").WithSummary("Create a unit under this one.");
        units.MapPost("/{id:guid}/rename", RenameAsync).RequirePermission(AuthorizationPermissions.UnitsManage).ScopedToUnit().Validate<RenameUnitRequest>().Produces<UnitResponse>(StatusCodes.Status200OK).WithName("RenameOrganisationUnit").WithSummary("Rename a unit.");
        units.MapPost("/{id:guid}/move", MoveAsync).RequirePermission(AuthorizationPermissions.UnitsManage).ScopedToUnit().Validate<MoveUnitRequest>().Produces<UnitResponse>(StatusCodes.Status200OK).WithName("MoveOrganisationUnit").WithSummary("Move a unit and its subtree under another unit.");
        units.MapPost("/{id:guid}/deactivate", DeactivateAsync).RequirePermission(AuthorizationPermissions.UnitsManage).ScopedToUnit().Produces<UnitResponse>(StatusCodes.Status200OK).WithName("DeactivateOrganisationUnit").WithSummary("Deactivate a unit that has no active units under it.");
        units.MapPost("/{id:guid}/reactivate", ReactivateAsync).RequirePermission(AuthorizationPermissions.UnitsManage).ScopedToUnit().Produces<UnitResponse>(StatusCodes.Status200OK).WithName("ReactivateOrganisationUnit").WithSummary("Make a deactivated unit active again, under an active parent; never a merged unit.");
        units.MapGet("/{id:guid}/readiness", ReadinessAsync).RequirePermission(AuthorizationPermissions.UnitsRead).ScopedToUnit().Produces<UnitReadinessResponse>(StatusCodes.Status200OK).WithName("GetOrganisationUnitReadiness").WithSummary("What the modules need of this unit before people work in it, and whether it is ready.");
        units.MapGet("/{id:guid}/merge-preview", MergePreviewAsync).RequirePermission(AuthorizationPermissions.UnitsManage).ScopedToUnit().Produces<MergeResponse>(StatusCodes.Status200OK).WithName("PreviewOrganisationUnitMerge").WithSummary("What merging this unit into the unit given by 'into' would move and drop, by module. Changes nothing.");
        units.MapPost("/{id:guid}/merge", MergeAsync).RequirePermission(AuthorizationPermissions.UnitsManage).ScopedToUnit().Validate<MergeUnitRequest>().Produces<MergeResponse>(StatusCodes.Status200OK).WithName("MergeOrganisationUnit").WithSummary("Merge this unit into another in one transaction: records, people, assignments, settings, shares and child units move; this unit ends inactive.");

        var kinds = endpoints.MapApiGroup("organisation-kinds");
        kinds.MapGet("/", Kinds).RequirePermission(AuthorizationPermissions.UnitsRead).ScopedByRow().Produces<KindsResponse>(StatusCodes.Status200OK).WithName("ListOrganisationKinds").WithSummary("The kinds of unit and which kinds each may hold.");
    }

    private static async Task<IResult> TreeAsync(OrganisationUnitService units, AccessGrants grants, ICurrentPrincipal reader, CancellationToken cancellationToken)
    {
        var access = await grants.AccessibleAsync(reader.Id, AuthorizationPermissions.UnitsRead, OrganisationUnitService.RecordType, cancellationToken);
        var tree = await units.TreeAsync(access, cancellationToken);
        return TypedResults.Ok(new UnitTreeResponse(tree.Select(UnitResponse.From).ToList()));
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, OrganisationUnitService units, CancellationToken cancellationToken)
    {
        var unit = await units.FindEntityAsync(id, cancellationToken);
        return unit is null
            ? ApiResults.Problem(Error.NotFound(AuthorizationErrors.UnitNotFound, "There is no organisation unit with this id."), http)
            : TypedResults.Ok(UnitResponse.From(unit));
    }

    private static async Task<IResult> CreateAsync(Guid id, CreateUnitRequest request, HttpContext http, OrganisationUnitService units, CancellationToken cancellationToken)
    {
        var result = await units.CreateAsync(id, request.Name, request.Code, request.Kind, cancellationToken);
        return ApiResults.From(result, http, unit => TypedResults.Created($"{ApiRouting.Prefix}/organisation-units/{unit.Id}", UnitResponse.From(unit)));
    }

    private static async Task<IResult> RenameAsync(Guid id, RenameUnitRequest request, HttpContext http, OrganisationUnitService units, CancellationToken cancellationToken)
    {
        var result = await units.RenameAsync(id, request.Name, cancellationToken);
        return ApiResults.From(result, http, unit => TypedResults.Ok(UnitResponse.From(unit)));
    }

    private static async Task<IResult> MoveAsync(Guid id, MoveUnitRequest request, HttpContext http, OrganisationUnitService units, IPermissionEvaluator evaluator, ICurrentPrincipal principal, CancellationToken cancellationToken)
    {
        // The filter checked the unit that moves; the new parent is in the body, so its check is here.
        var atParent = new AccessRequest(principal.Id, AuthorizationPermissions.UnitsManage, PermissionFilter.Operation(http), http.Request.Path.Value ?? "/") { Scope = AccessScope.Unit(request.ParentId) };
        var decision = await evaluator.EvaluateAsync(atParent, cancellationToken);
        if (!decision.Allowed)
        {
            return Denied(http, "You do not have the permission to move a unit under this one.");
        }

        var result = await units.MoveAsync(id, request.ParentId, cancellationToken);
        return ApiResults.From(result, http, unit => TypedResults.Ok(UnitResponse.From(unit)));
    }

    private static async Task<IResult> ReactivateAsync(Guid id, HttpContext http, OrganisationUnitService units, CancellationToken cancellationToken)
    {
        var result = await units.ReactivateAsync(id, cancellationToken);
        return ApiResults.From(result, http, unit => TypedResults.Ok(UnitResponse.From(unit)));
    }

    private static async Task<IResult> ReadinessAsync(Guid id, HttpContext http, OrganisationUnitService units, IEnumerable<IUnitReadiness> checklists, CancellationToken cancellationToken)
    {
        var unit = await units.FindAsync(id, cancellationToken);
        if (unit is null)
        {
            return ApiResults.Problem(Error.NotFound(AuthorizationErrors.UnitNotFound, "There is no organisation unit with this id."), http);
        }

        var items = new List<ReadinessItem>();
        foreach (var checklist in checklists)
        {
            items.AddRange(await checklist.CheckAsync(unit, cancellationToken));
        }

        return TypedResults.Ok(new UnitReadinessResponse(unit.Id, items.TrueForAll(i => i.Done), items));
    }

    private static async Task<IResult> MergePreviewAsync(Guid id, Guid? into, HttpContext http, UnitMergeService merges, IPermissionEvaluator evaluator, ICurrentPrincipal principal, CancellationToken cancellationToken)
    {
        if (into is not { } intoId)
        {
            return ApiResults.Problem(Error.Validation(ApiErrors.Validation, "Say which unit to merge into.", new FieldError("into", "required", "Choose the unit to merge into.")), http);
        }

        if (!await MayManageAsync(intoId, http, evaluator, principal, cancellationToken))
        {
            return Denied(http, "You do not have the permission to merge a unit into this one.");
        }

        var result = await merges.PreviewAsync(id, intoId, cancellationToken);
        return ApiResults.From(result, http, outcome => TypedResults.Ok(MergeResponse.Of(outcome)));
    }

    private static async Task<IResult> MergeAsync(Guid id, MergeUnitRequest request, HttpContext http, UnitMergeService merges, IPermissionEvaluator evaluator, ICurrentPrincipal principal, CancellationToken cancellationToken)
    {
        // The filter checked the unit that goes; the unit that stays is in the body, so its check is here (ADR-0038).
        if (!await MayManageAsync(request.IntoId, http, evaluator, principal, cancellationToken))
        {
            return Denied(http, "You do not have the permission to merge a unit into this one.");
        }

        var result = await merges.MergeAsync(id, request.IntoId, cancellationToken);
        return ApiResults.From(result, http, outcome => TypedResults.Ok(MergeResponse.Of(outcome)));
    }

    private static async Task<bool> MayManageAsync(Guid unitId, HttpContext http, IPermissionEvaluator evaluator, ICurrentPrincipal principal, CancellationToken cancellationToken)
    {
        var request = new AccessRequest(principal.Id, AuthorizationPermissions.UnitsManage, PermissionFilter.Operation(http), http.Request.Path.Value ?? "/") { Scope = AccessScope.Unit(unitId) };
        return (await evaluator.EvaluateAsync(request, cancellationToken)).Allowed;
    }

    private static IResult Denied(HttpContext http, string message)
    {
        var problem = ProblemFactory.Create(Error.Forbidden(PermissionErrors.PermissionDenied, message), http);
        problem.Extensions["permission"] = AuthorizationPermissions.UnitsManage.Name;
        return TypedResults.Problem(problem);
    }

    private static async Task<IResult> DeactivateAsync(Guid id, HttpContext http, OrganisationUnitService units, CancellationToken cancellationToken)
    {
        var result = await units.DeactivateAsync(id, cancellationToken);
        return ApiResults.From(result, http, unit => TypedResults.Ok(UnitResponse.From(unit)));
    }

    private static IResult Kinds(IOrganisationKinds kinds) =>
        TypedResults.Ok(new KindsResponse(kinds.Kinds.Select(k => new KindResponse(k.Code, k.Name, k.ChildKinds)).ToList(), kinds.RootKind));
}
