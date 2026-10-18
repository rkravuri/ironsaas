using Iron.Api.Lists;
using Iron.Api.Permissions;
using Iron.Api.Responses;
using Iron.Api.Routing;
using Iron.Data.Lists;
using Iron.Identity.Contracts;
using Iron.Identity.Data;
using Iron.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Identity.Api;

/// <summary>What a client sees of a principal: never a hash, a token or a counter.</summary>
public sealed record UserResponse(
    Guid Id,
    string LoginId,
    string DisplayName,
    PrincipalType Type,
    PrincipalStatus Status,
    string? Email,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    public static UserResponse From(Principal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new UserResponse(principal.Id, principal.LoginId, principal.DisplayName, principal.Type, principal.Status, principal.Email, principal.LastLoginAt, principal.CreatedAt, principal.UpdatedAt, principal.Version);
    }
}

/// <summary><c>GET /api/v1/users</c>: the one protected operation of S1, with the list grammar (TF-101).</summary>
internal static class UsersEndpoints
{
    public static readonly ListFields<Principal> Fields = new ListFields<Principal>()
        .Add("id", p => p.Id, FieldUse.Select | FieldUse.Filter)
        .Add("login_id", p => p.LoginId)
        .Add("display_name", p => p.DisplayName)
        .Add("type", p => p.Type, FieldUse.Select | FieldUse.Filter)
        .Add("status", p => p.Status, FieldUse.Select | FieldUse.Filter)
        .Add("email", p => p.Email, FieldUse.Select | FieldUse.Filter)
        .Add("last_login_at", p => p.LastLoginAt, FieldUse.Select | FieldUse.Sort)
        .Add("created_at", p => p.CreatedAt, FieldUse.Select | FieldUse.Sort)
        .Add("updated_at", p => p.UpdatedAt, FieldUse.Select | FieldUse.Sort)
        .Add("version", p => p.Version, FieldUse.Select)
        .SortByDefault("login_id");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var users = endpoints.MapApiGroup("users").RequirePermission(IdentityPermissions.UsersRead);
        users.MapGet("/", ListAsync).WithName("ListUsers").WithSummary("List the users and service accounts of this tenant.");
    }

    private static async Task<IResult> ListAsync(HttpContext http, IdentityDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.Principals.AsNoTracking().ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(UserResponse.From), http, query);
    }
}
