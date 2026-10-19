using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data.Lists;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Api;

/// <summary>A grant: who, which role, and where. A unit scope names the unit; a record scope names the record type and id.</summary>
public sealed record GrantRoleRequest(Guid PrincipalId, Guid RoleId, RoleScopeKind ScopeKind, Guid? UnitId, string? RecordType, Guid? RecordId)
{
    public RoleScope ToScope() => ScopeKind switch
    {
        RoleScopeKind.Unit => RoleScope.Unit(UnitId!.Value),
        RoleScopeKind.UnitWithChildren => RoleScope.Unit(UnitId!.Value, includeChildren: true),
        RoleScopeKind.Record => RoleScope.Record(RecordType!.Trim(), RecordId!.Value),
        _ => RoleScope.Tenant,
    };
}

public sealed class GrantRoleRequestValidator : AbstractValidator<GrantRoleRequest>
{
    public GrantRoleRequestValidator()
    {
        RuleFor(r => r.PrincipalId).NotEmpty();
        RuleFor(r => r.RoleId).NotEmpty();
        RuleFor(r => r.ScopeKind).IsInEnum();
        When(r => r.ScopeKind is RoleScopeKind.Unit or RoleScopeKind.UnitWithChildren, () => RuleFor(r => r.UnitId).NotNull().NotEqual(Guid.Empty).WithMessage("Choose a unit."));
        When(r => r.ScopeKind is RoleScopeKind.Record, () =>
        {
            RuleFor(r => r.RecordType).NotEmpty().MaximumLength(RoleScope.RecordTypeMaxLength);
            RuleFor(r => r.RecordId).NotNull().NotEqual(Guid.Empty);
        });
    }
}

/// <summary>
/// Role assignments (TF-033, ADR-0031): listed for a principal or a unit, granted and removed. The permission filter
/// lets through anyone who holds the permission somewhere (<c>ScopedByRow</c>); the service decides at the scope the
/// assignment names, and a list keeps the assignments the reader may see.
/// </summary>
internal static class RoleAssignmentsEndpoints
{
    /// <summary>The whitelist of the list grammar (TF-093): filter by principal, role, scope kind or unit.</summary>
    public static readonly ListFields<RoleAssignment> Fields = new ListFields<RoleAssignment>()
        .Add("id", a => a.Id, FieldUse.Select | FieldUse.Filter)
        .Add("principal_id", a => a.PrincipalId, FieldUse.Select | FieldUse.Filter)
        .Add("role_id", a => a.RoleId, FieldUse.Select | FieldUse.Filter)
        .Add("scope_kind", a => a.ScopeKind, FieldUse.Select | FieldUse.Filter)
        .Add("unit_id", a => a.ScopeKind == Scopes.Unit || a.ScopeKind == Scopes.UnitWithChildren ? a.ScopeId : null, FieldUse.Select | FieldUse.Filter)
        .Add("created_at", a => a.CreatedAt)
        .SortByDefault("created_at");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var assignments = endpoints.MapApiGroup("role-assignments");
        assignments.MapGet("/", ListAsync).RequirePermission(AuthorizationPermissions.AssignmentsRead).ScopedByRow().WithName("ListRoleAssignments").WithSummary("The role assignments you may see, for a principal (filter=principal_id:eq:<id>) or a unit (filter=unit_id:eq:<id>).");
        assignments.MapGet("/{id:guid}", GetAsync).RequirePermission(AuthorizationPermissions.AssignmentsRead).ScopedByRow().WithName("GetRoleAssignment").WithSummary("One role assignment you may see.");
        assignments.MapPost("/", GrantAsync).RequirePermission(AuthorizationPermissions.AssignmentsManage).ScopedByRow().Validate<GrantRoleRequest>().WithName("GrantRole").WithSummary("Grant a role to a principal for the tenant, a unit, a unit with its children, or a record.");
        assignments.MapDelete("/{id:guid}", RemoveAsync).RequirePermission(AuthorizationPermissions.AssignmentsManage).ScopedByRow().WithName("RemoveRoleAssignment").WithSummary("Remove a role assignment.");
    }

    private static async Task<IResult> ListAsync(HttpContext http, AssignmentService assignments, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var visible = await assignments.VisibleAsync(cancellationToken);
        var page = await visible.ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        var views = (await assignments.DescribeAsync(page.Items, cancellationToken)).ToDictionary(v => v.Id);
        return ApiResults.List(page.Map(a => views[a.Id]), http, query);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, AssignmentService assignments, CancellationToken cancellationToken)
    {
        var visible = await assignments.VisibleAsync(cancellationToken);
        var row = await visible.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        return row is null
            ? ApiResults.Problem(Error.NotFound(AuthorizationErrors.AssignmentNotFound, "There is no role assignment with this id that you may see."), http)
            : TypedResults.Ok((await assignments.DescribeAsync([row], cancellationToken))[0]);
    }

    private static async Task<IResult> GrantAsync(GrantRoleRequest request, HttpContext http, AssignmentService assignments, CancellationToken cancellationToken)
    {
        var result = await assignments.GrantAsync(request.PrincipalId, request.RoleId, request.ToScope(), cancellationToken);
        return ApiResults.From(result, http, view => TypedResults.Created($"{ApiRouting.Prefix}/role-assignments/{view.Id}", view));
    }

    private static async Task<IResult> RemoveAsync(Guid id, HttpContext http, AssignmentService assignments, CancellationToken cancellationToken)
    {
        var result = await assignments.RemoveAsync(id, cancellationToken);
        return ApiResults.From(result, http, () => TypedResults.NoContent());
    }
}
