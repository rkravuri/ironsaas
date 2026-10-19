using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Lists;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Api;

/// <summary>What a client sees of a role. Property names in snake_case are the field names of the list grammar.</summary>
public sealed record RoleResponse(
    Guid Id,
    string Name,
    string Description,
    bool IsSystem,
    PrincipalType PrincipalType,
    IReadOnlyList<string> Permissions,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    public static RoleResponse From(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return new RoleResponse(role.Id, role.Name, role.Description, role.IsSystem, role.PrincipalType, role.Permissions, role.CreatedAt, role.UpdatedAt, role.Version);
    }
}

internal static class RolesEndpoints
{
    /// <summary>The whitelist of the list grammar (TF-093): what can be selected, filtered and sorted.</summary>
    public static readonly ListFields<Role> Fields = new ListFields<Role>()
        .Add("id", r => r.Id, FieldUse.Select | FieldUse.Filter)
        .Add("name", r => r.Name)
        .Add("description", r => r.Description, FieldUse.Select)
        .Add("is_system", r => r.IsSystem, FieldUse.Select | FieldUse.Filter)
        .Add("principal_type", r => r.PrincipalType, FieldUse.Select | FieldUse.Filter)
        .Add("permissions", r => r.Permissions, FieldUse.Select)
        .Add("created_at", r => r.CreatedAt, FieldUse.Select | FieldUse.Sort)
        .Add("updated_at", r => r.UpdatedAt, FieldUse.Select | FieldUse.Sort)
        .Add("version", r => r.Version, FieldUse.Select)
        .SortByDefault("name");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var roles = endpoints.MapApiGroup("roles").RequirePermission(AuthorizationPermissions.RolesRead);
        roles.MapGet("/", ListAsync).WithName("ListRoles").WithSummary("List the roles of this tenant.");
    }

    private static async Task<IResult> ListAsync(HttpContext http, AuthorizationDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.Roles.AsNoTracking().ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(RoleResponse.From), http, query);
    }
}
