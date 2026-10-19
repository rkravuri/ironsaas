using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Problems;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Data.Policies;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Api;

/// <summary>What a client sees of a principal: never a hash, a token or a counter.</summary>
public sealed record UserResponse(
    Guid Id,
    string LoginId,
    string DisplayName,
    PrincipalType Type,
    PrincipalStatus Status,
    string? Email,
    Guid? HomeUnitId,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    public static UserResponse From(Principal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new UserResponse(principal.Id, principal.LoginId, principal.DisplayName, principal.Type, principal.Status, principal.Email, principal.HomeUnitId, principal.LastLoginAt, principal.CreatedAt, principal.UpdatedAt, principal.Version);
    }
}

/// <summary>One user as the user record page shows it: the list's fields and the home unit by name.</summary>
public sealed record UserRecordResponse(
    Guid Id,
    string LoginId,
    string DisplayName,
    PrincipalType Type,
    PrincipalStatus Status,
    string? Email,
    HomeUnitResponse? HomeUnit,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);

public sealed record CreateUserRequest(string LoginId, string DisplayName, Guid HomeUnitId);

/// <summary>The user created and the invitation link to pass on; email delivery arrives in S4.</summary>
public sealed record CreateUserResponse(UserResponse User, string InvitationLink, DateTimeOffset InvitationExpiresAt);

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(r => r.LoginId).NotEmpty().MaximumLength(Principal.LoginIdMaxLength);
        RuleFor(r => r.DisplayName).NotEmpty().MaximumLength(Principal.DisplayNameMaxLength);
        RuleFor(r => r.HomeUnitId).NotEmpty();
    }
}

/// <summary>
/// Users by scope (A2, ADR-0031): the list and the record keep the principals whose home unit the reader holds
/// <c>identity.users.read</c> at, and a user outside answers 404 so neither reveals who exists. Creating a user needs
/// <c>identity.users.manage</c> at the new user's home unit.
/// </summary>
internal static class UsersEndpoints
{
    public static readonly ListFields<Principal> Fields = new ListFields<Principal>()
        .Add("id", p => p.Id, FieldUse.Select | FieldUse.Filter)
        .Add("login_id", p => p.LoginId)
        .Add("display_name", p => p.DisplayName)
        .Add("type", p => p.Type, FieldUse.Select | FieldUse.Filter)
        .Add("status", p => p.Status, FieldUse.Select | FieldUse.Filter)
        .Add("email", p => p.Email, FieldUse.Select | FieldUse.Filter)
        .Add("home_unit_id", p => p.HomeUnitId, FieldUse.Select | FieldUse.Filter)
        .Add("last_login_at", p => p.LastLoginAt, FieldUse.Select | FieldUse.Sort)
        .Add("created_at", p => p.CreatedAt, FieldUse.Select | FieldUse.Sort)
        .Add("updated_at", p => p.UpdatedAt, FieldUse.Select | FieldUse.Sort)
        .Add("version", p => p.Version, FieldUse.Select)
        .SortByDefault("login_id");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var users = endpoints.MapApiGroup("users");
        users.MapGet("/", ListAsync).RequirePermission(IdentityPermissions.UsersRead).ScopedByRow().WithName("ListUsers").WithSummary("List the users and service accounts you may see.");
        users.MapGet("/{id:guid}", GetAsync).RequirePermission(IdentityPermissions.UsersRead).ScopedByRow().WithName("GetUser").WithSummary("One user or service account you may see.");
        users.MapPost("/", CreateAsync).RequirePermission(IdentityPermissions.UsersManage).ScopedByRow().Validate<CreateUserRequest>().WithName("CreateUser").WithSummary("Create a user in a unit and answer the invitation link.");
    }

    /// <summary>The principals whose home unit the reader holds <c>identity.users.read</c> at; one predicate in the query.</summary>
    internal static async Task<IQueryable<Principal>> VisibleAsync(IdentityDbContext context, IRecordAccess access, ICurrentPrincipal reader, CancellationToken cancellationToken)
    {
        var reach = await access.AccessibleAsync(reader.Id, IdentityPermissions.UsersRead, UserService.RecordType, cancellationToken);
        return context.Principals.AsNoTracking().WhereAccessible(reach, p => p.HomeUnitId, p => p.Id);
    }

    private static async Task<IResult> ListAsync(HttpContext http, IdentityDbContext context, IRecordAccess access, ICurrentPrincipal reader, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var visible = await VisibleAsync(context, access, reader, cancellationToken);
        var page = await visible.ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(UserResponse.From), http, query);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, IdentityDbContext context, IRecordAccess access, ICurrentPrincipal reader, IOrganisationUnits units, CancellationToken cancellationToken)
    {
        var visible = await VisibleAsync(context, access, reader, cancellationToken);
        var principal = await visible.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (principal is null)
        {
            return ApiResults.Problem(Error.NotFound(IdentityErrors.UserNotFound, "There is no user with this id that you may see."), http);
        }

        var home = principal.HomeUnitId is { } unitId ? await units.FindAsync(unitId, cancellationToken) : null;
        return TypedResults.Ok(new UserRecordResponse(
            principal.Id,
            principal.LoginId,
            principal.DisplayName,
            principal.Type,
            principal.Status,
            principal.Email,
            home is null ? null : new HomeUnitResponse(home.Id, home.Name, home.Code),
            principal.LastLoginAt,
            principal.CreatedAt,
            principal.UpdatedAt,
            principal.Version));
    }

    private static async Task<IResult> CreateAsync(CreateUserRequest request, HttpContext http, IPermissionEvaluator evaluator, ICurrentPrincipal actor, UserService users, CancellationToken cancellationToken)
    {
        // The permission is checked at the unit the request names, which the route does not carry (ADR-0031).
        var check = new AccessRequest(actor.Id, IdentityPermissions.UsersManage, PermissionFilter.Operation(http), http.Request.Path.Value ?? "/") { Scope = AccessScope.Unit(request.HomeUnitId) };
        var decision = await evaluator.EvaluateAsync(check, cancellationToken);
        if (!decision.Allowed)
        {
            var problem = ProblemFactory.Create(Error.Forbidden(PermissionErrors.PermissionDenied, "You cannot create users in this unit."), http);
            problem.Extensions["permission"] = IdentityPermissions.UsersManage.Name;
            return TypedResults.Problem(problem);
        }

        var result = await users.CreateAsync(request.LoginId, request.DisplayName, request.HomeUnitId, cancellationToken);
        return ApiResults.From(result, http, created => TypedResults.Created(
            $"{ApiRouting.Prefix}/users/{created.Principal.Id}",
            new CreateUserResponse(UserResponse.From(created.Principal), $"/invitations/{created.InvitationToken}", created.InvitationExpiresAt)));
    }
}
