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
    int Version)
{
    public static UnitResponse From(OrganisationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return new UnitResponse(unit.Id, unit.Name, unit.Code, unit.Kind, unit.ParentId, unit.Depth, unit.Status, unit.CreatedAt, unit.UpdatedAt, unit.Version);
    }
}

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

public sealed class MoveUnitRequestValidator : AbstractValidator<MoveUnitRequest>
{
    public MoveUnitRequestValidator()
    {
        RuleFor(r => r.ParentId).NotEmpty();
    }
}

/// <summary>
/// The organisation tree (TF-034): read it whole, read one unit, create a unit under another, rename, move and
/// deactivate. A change needs <c>authz.units.manage</c> at the unit it touches (ADR-0030): a role assigned to a unit with
/// its children lets its holder shape that part of the tree, and a move also needs it at the new parent.
/// </summary>
internal static class OrganisationUnitsEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var units = endpoints.MapApiGroup("organisation-units");
        units.MapGet("/", TreeAsync).RequirePermission(AuthorizationPermissions.UnitsRead).ScopedByRow().WithName("ListOrganisationUnits").WithSummary("The part of the organisation tree you may see, depth first.");
        units.MapGet("/{id:guid}", GetAsync).RequirePermission(AuthorizationPermissions.UnitsRead).ScopedToUnit().WithName("GetOrganisationUnit").WithSummary("One organisation unit.");
        units.MapPost("/{id:guid}/children", CreateAsync).RequirePermission(AuthorizationPermissions.UnitsManage).ScopedToUnit().Validate<CreateUnitRequest>().WithName("CreateOrganisationUnit").WithSummary("Create a unit under this one.");
        units.MapPost("/{id:guid}/rename", RenameAsync).RequirePermission(AuthorizationPermissions.UnitsManage).ScopedToUnit().Validate<RenameUnitRequest>().WithName("RenameOrganisationUnit").WithSummary("Rename a unit.");
        units.MapPost("/{id:guid}/move", MoveAsync).RequirePermission(AuthorizationPermissions.UnitsManage).ScopedToUnit().Validate<MoveUnitRequest>().WithName("MoveOrganisationUnit").WithSummary("Move a unit and its subtree under another unit.");
        units.MapPost("/{id:guid}/deactivate", DeactivateAsync).RequirePermission(AuthorizationPermissions.UnitsManage).ScopedToUnit().WithName("DeactivateOrganisationUnit").WithSummary("Deactivate a unit that has no active units under it.");

        var kinds = endpoints.MapApiGroup("organisation-kinds");
        kinds.MapGet("/", Kinds).RequirePermission(AuthorizationPermissions.UnitsRead).ScopedByRow().WithName("ListOrganisationKinds").WithSummary("The kinds of unit and which kinds each may hold.");
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
            var problem = ProblemFactory.Create(Error.Forbidden(PermissionErrors.PermissionDenied, "You do not have the permission to move a unit under this one."), http);
            problem.Extensions["permission"] = AuthorizationPermissions.UnitsManage.Name;
            return TypedResults.Problem(problem);
        }

        var result = await units.MoveAsync(id, request.ParentId, cancellationToken);
        return ApiResults.From(result, http, unit => TypedResults.Ok(UnitResponse.From(unit)));
    }

    private static async Task<IResult> DeactivateAsync(Guid id, HttpContext http, OrganisationUnitService units, CancellationToken cancellationToken)
    {
        var result = await units.DeactivateAsync(id, cancellationToken);
        return ApiResults.From(result, http, unit => TypedResults.Ok(UnitResponse.From(unit)));
    }

    private static IResult Kinds(IOrganisationKinds kinds) =>
        TypedResults.Ok(new KindsResponse(kinds.Kinds.Select(k => new KindResponse(k.Code, k.Name, k.ChildKinds)).ToList(), kinds.RootKind));
}
