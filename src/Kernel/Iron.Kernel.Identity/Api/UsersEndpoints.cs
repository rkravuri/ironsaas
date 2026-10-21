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
using Microsoft.Extensions.DependencyInjection;

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

/// <summary>
/// One user as the user record page shows it: the list's fields, the home unit by name, and until when their login is
/// locked (TF-012), null when it is not, so an administrator sees why they cannot sign in and may unlock it.
/// </summary>
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
    int Version,
    DateTimeOffset? LockedUntil = null);

/// <summary>A recovery link for the administrator to pass on, shown once (TF-017): its path and when it stops working.</summary>
public sealed record RecoveryLinkResponse(string RecoveryLink, DateTimeOffset ExpiresAt);

/// <summary>A user reactivated, and the new invitation's link when they never set a password.</summary>
public sealed record ReactivateUserResponse(UserResponse User, string? InvitationLink, DateTimeOffset? InvitationExpiresAt);

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
/// <c>identity.users.manage</c> at the new user's home unit; so do unlocking, a recovery link, deactivating and
/// reactivating, at the person's home unit (T2, ADR-0046).
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
        users.MapGet("/", ListAsync).RequirePermission(IdentityPermissions.UsersRead).ScopedByRow().Produces<ListResponse<UserResponse>>(StatusCodes.Status200OK).WithName("ListUsers").WithSummary("List the users and service accounts you may see.");
        users.MapGet("/{id:guid}", GetAsync).RequirePermission(IdentityPermissions.UsersRead).ScopedByRow().Produces<UserRecordResponse>(StatusCodes.Status200OK).WithName("GetUser").WithSummary("One user or service account you may see.");
        users.MapPost("/", CreateAsync).RequirePermission(IdentityPermissions.UsersManage).ScopedByRow().Validate<CreateUserRequest>().Produces<CreateUserResponse>(StatusCodes.Status201Created).WithName("CreateUser").WithSummary("Create a user in a unit and answer the invitation link.");
        users.MapPost("/{id:guid}/unlock", UnlockAsync).RequirePermission(IdentityPermissions.UsersManage).ScopedByRow().Produces<UserRecordResponse>(StatusCodes.Status200OK).WithName("UnlockUser").WithSummary("Lift the lock on a person's login after too many failed sign-ins.");
        users.MapPost("/{id:guid}/recovery-link", RecoveryLinkAsync).RequirePermission(IdentityPermissions.UsersManage).ScopedByRow().Produces<RecoveryLinkResponse>(StatusCodes.Status200OK).WithName("IssueRecoveryLink").WithSummary("Issue a recovery link for a person, shown once: one use, 15 minutes; it replaces any earlier link.");
        users.MapPost("/{id:guid}/deactivate", DeactivateAsync).RequirePermission(IdentityPermissions.UsersManage).ScopedByRow().Produces<UserRecordResponse>(StatusCodes.Status200OK).WithName("DeactivateUser").WithSummary("Deactivate a person: they cannot sign in, their sessions end and their tokens are refused.");
        users.MapPost("/{id:guid}/reactivate", ReactivateAsync).RequirePermission(IdentityPermissions.UsersManage).ScopedByRow().Produces<ReactivateUserResponse>(StatusCodes.Status200OK).WithName("ReactivateUser").WithSummary("Reactivate a person; one who never set a password gets a new invitation.");
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

        // The list keeps to the unit the person works for and the units below it, unless they see all their units
        // (ADR-0038). A user opened by id is not narrowed: a link works from any unit the reader may see.
        if (http.RequestServices.GetService<IUnitReach>() is { } units && await units.WorkingUnitsAsync(reader.Id, cancellationToken) is { } working)
        {
            var workingIds = working.ToArray();
            visible = visible.Where(p => p.HomeUnitId != null && workingIds.Contains(p.HomeUnitId.Value));
        }

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

        return TypedResults.Ok(await RecordAsync(principal, units, http.RequestServices.GetRequiredService<LoginThrottles>(), cancellationToken));
    }

    private static async Task<UserRecordResponse> RecordAsync(Principal principal, IOrganisationUnits units, LoginThrottles throttles, CancellationToken cancellationToken)
    {
        var home = principal.HomeUnitId is { } unitId ? await units.FindAsync(unitId, cancellationToken) : null;
        return new UserRecordResponse(
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
            principal.Version,
            await throttles.LockedUntilAsync(principal.LoginId, cancellationToken));
    }

    /// <summary>
    /// The person the request names, when the actor may see them (404 otherwise, as for the record) and holds
    /// <c>identity.users.manage</c> at their home unit (403 otherwise, naming the permission). Tracked, for the change.
    /// </summary>
    private static async Task<Result<Principal>> ManageableAsync(Guid id, HttpContext http, string doing, CancellationToken cancellationToken)
    {
        var services = http.RequestServices;
        var context = services.GetRequiredService<IdentityDbContext>();
        var actor = services.GetRequiredService<ICurrentPrincipal>();
        var visible = await VisibleAsync(context, services.GetRequiredService<IRecordAccess>(), actor, cancellationToken);
        if (!await visible.AnyAsync(p => p.Id == id, cancellationToken))
        {
            return Error.NotFound(IdentityErrors.UserNotFound, "There is no user with this id that you may see.");
        }

        var principal = await context.Principals.SingleAsync(p => p.Id == id, cancellationToken);
        var scope = principal.HomeUnitId is { } unit ? AccessScope.Unit(unit) : AccessScope.Tenant;
        var check = new AccessRequest(actor.Id, IdentityPermissions.UsersManage, PermissionFilter.Operation(http), http.Request.Path.Value ?? "/") { Scope = scope };
        var decision = await services.GetRequiredService<IPermissionEvaluator>().EvaluateAsync(check, cancellationToken);
        return decision.Allowed ? principal : Error.Forbidden(PermissionErrors.PermissionDenied, $"You cannot {doing} people in {principal.DisplayName}'s unit.");
    }

    private static IResult Refused(Error error, HttpContext http)
    {
        var problem = ProblemFactory.Create(error, http);
        if (error.Code == PermissionErrors.PermissionDenied)
        {
            problem.Extensions["permission"] = IdentityPermissions.UsersManage.Name;
        }

        return TypedResults.Problem(problem);
    }

    private static async Task<IResult> UnlockAsync(Guid id, HttpContext http, UserService users, IOrganisationUnits units, LoginThrottles throttles, CancellationToken cancellationToken)
    {
        var target = await ManageableAsync(id, http, "unlock", cancellationToken);
        if (target.IsFailure)
        {
            return Refused(target.Error, http);
        }

        var result = await users.UnlockAsync(target.Value, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok(await RecordAsync(result.Value, units, throttles, cancellationToken));
    }

    private static async Task<IResult> RecoveryLinkAsync(Guid id, HttpContext http, UserService users, CancellationToken cancellationToken)
    {
        var target = await ManageableAsync(id, http, "issue recovery links for", cancellationToken);
        if (target.IsFailure)
        {
            return Refused(target.Error, http);
        }

        var result = await users.IssueRecoveryAsync(target.Value, cancellationToken);
        return ApiResults.From(result, http, link => TypedResults.Ok(new RecoveryLinkResponse($"/recovery/{link.Token}", link.ExpiresAt)));
    }

    private static async Task<IResult> DeactivateAsync(Guid id, HttpContext http, UserService users, IOrganisationUnits units, LoginThrottles throttles, CancellationToken cancellationToken)
    {
        var target = await ManageableAsync(id, http, "deactivate", cancellationToken);
        if (target.IsFailure)
        {
            return Refused(target.Error, http);
        }

        var result = await users.DeactivateAsync(target.Value, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok(await RecordAsync(result.Value, units, throttles, cancellationToken));
    }

    private static async Task<IResult> ReactivateAsync(Guid id, HttpContext http, UserService users, CancellationToken cancellationToken)
    {
        var target = await ManageableAsync(id, http, "reactivate", cancellationToken);
        if (target.IsFailure)
        {
            return Refused(target.Error, http);
        }

        var result = await users.ReactivateAsync(target.Value, cancellationToken);
        return ApiResults.From(result, http, done => TypedResults.Ok(new ReactivateUserResponse(
            UserResponse.From(done.Principal),
            done.InvitationToken is { } token ? $"/invitations/{token}" : null,
            done.InvitationToken is null ? null : done.Principal.InvitationExpiresAt)));
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
