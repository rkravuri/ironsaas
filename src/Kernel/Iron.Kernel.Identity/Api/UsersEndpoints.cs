using FluentValidation;
using Iron.Kernel.Api.Http;
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
    DateTimeOffset? LockedUntil = null,
    int Factors = 0,
    bool TakeoverAllowed = false,
    bool SsoOnly = false,
    bool SsoOnlyByProvider = false,
    IReadOnlyList<SsoLinkResponse>? SsoLinks = null,
    bool Emergency = false,
    DateTimeOffset? BreakGlassUntil = null);

/// <summary>One of the tenant's emergency accounts, or not (ADR-0049).</summary>
public sealed record SetEmergencyRequest(bool Emergency);

/// <summary>An emergency account's sign-in, for the administrators' banner (ADR-0049).</summary>
public sealed record EmergencySignInResponse(Guid Id, Guid PrincipalId, string DisplayName, string LoginId, DateTimeOffset At);

/// <summary>The emergency sign-ins of the last day.</summary>
public sealed record SecurityAlertsResponse(IReadOnlyList<EmergencySignInResponse> EmergencySignIns);

/// <summary>A person's link to a single sign-on provider, as the user record shows it (ADR-0048).</summary>
public sealed record SsoLinkResponse(Guid ProviderId, string ProviderName, string Email, DateTimeOffset LinkedAt, DateTimeOffset LastSignInAt);

/// <summary>Sign in only through a provider, or with a password as well (TF-014 AC2).</summary>
public sealed record SetSsoOnlyRequest(bool SsoOnly);

/// <summary>Why an administrator resets a person's factors: required, and kept in the person's security activity.</summary>
public sealed record ResetFactorsRequest(string Reason);

public sealed class ResetFactorsRequestValidator : AbstractValidator<ResetFactorsRequest>
{
    public ResetFactorsRequestValidator()
    {
        RuleFor(r => r.Reason).NotEmpty().MaximumLength(SecurityEvent.ReasonMaxLength);
    }
}

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
        users.MapPost("/{id:guid}/recovery-link", RecoveryLinkAsync).RequirePermission(IdentityPermissions.UsersManage).RateLimited(RateLimitPolicies.Recovery, "id").ScopedByRow().RequireRecentAuthentication().Produces<RecoveryLinkResponse>(StatusCodes.Status200OK).WithName("IssueRecoveryLink").WithSummary("Issue a recovery link for a person, shown once: one use, 15 minutes; it replaces any earlier link.");
        users.MapPost("/{id:guid}/factors/reset", ResetFactorsAsync).RequirePermission(IdentityPermissions.UsersManage).ScopedByRow().RequireRecentAuthentication().Validate<ResetFactorsRequest>().Produces<UserRecordResponse>(StatusCodes.Status200OK).WithName("ResetFactors").WithSummary("Remove a person's second factors and recovery codes, with a reason, for someone whose permissions you hold; their sessions end.");
        users.MapPost("/{id:guid}/deactivate", DeactivateAsync).RequirePermission(IdentityPermissions.UsersManage).ScopedByRow().RequireRecentAuthentication().Produces<UserRecordResponse>(StatusCodes.Status200OK).WithName("DeactivateUser").WithSummary("Deactivate a person: they cannot sign in, their sessions end and their tokens are refused.");
        users.MapPut("/{id:guid}/sso-only", SetSsoOnlyAsync).RequirePermission(IdentityPermissions.UsersManage).ScopedByRow().RequireRecentAuthentication().Produces<UserRecordResponse>(StatusCodes.Status200OK).WithName("SetSsoOnly").WithSummary("Mark a person to sign in only through a single sign-on provider, or let them use a password again.");
        users.MapPut("/{id:guid}/emergency", SetEmergencyAsync).RequirePermission(IdentityPermissions.UsersManage).ScopedByRow().RequireRecentAuthentication().Produces<UserRecordResponse>(StatusCodes.Status200OK).WithName("SetEmergency").WithSummary("Mark a person's account as one of the tenant's two emergency accounts, which sign in with a password and a second factor when the company login is down, or remove the mark.");
        endpoints.MapGet("/api/v1/security-alerts", AlertsAsync).WithTags("users").RequirePermission(IdentityPermissions.UsersManage).Produces<SecurityAlertsResponse>(StatusCodes.Status200OK).WithName("SecurityAlerts").WithSummary("What the tenant's administrators must look at: the emergency accounts' sign-ins of the last 24 hours.");
        users.MapPost("/{id:guid}/revoke", RevokeAsync).RequirePermission(IdentityPermissions.UsersManage).ScopedByRow().RequireRecentAuthentication().Validate<RevokeRequest>().Produces<SessionsEndedResponse>(StatusCodes.Status200OK).WithName("RevokePrincipal").WithSummary("Revoke everything of a person or a service account, with a reason: every session ends now and no token issued until now is accepted.");
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

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, IdentityDbContext context, IRecordAccess access, ICurrentPrincipal reader, CancellationToken cancellationToken)
    {
        var visible = await VisibleAsync(context, access, reader, cancellationToken);
        var principal = await visible.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (principal is null)
        {
            return ApiResults.Problem(Error.NotFound(IdentityErrors.UserNotFound, "There is no user with this id that you may see."), http);
        }

        return TypedResults.Ok(await RecordAsync(principal, http.RequestServices, cancellationToken));
    }

    private static async Task<UserRecordResponse> RecordAsync(Principal principal, IServiceProvider services, CancellationToken cancellationToken)
    {
        var home = principal.HomeUnitId is { } unitId ? await services.GetRequiredService<IOrganisationUnits>().FindAsync(unitId, cancellationToken) : null;

        // How many factors the person has, and whether the reader holds every permission the person holds, so the record
        // offers "Reset factors" and the recovery link only where the escalation rule lets the reader use them (ADR-0047).
        var factors = await services.GetRequiredService<IdentityDbContext>().Factors.CountAsync(f => f.PrincipalId == principal.Id, cancellationToken);
        var takeover = principal.Type == PrincipalType.User && await services.GetRequiredService<UserService>().MayTakeOverAsync(principal.Id, cancellationToken);
        var people = services.GetRequiredService<SsoPeople>();
        var ssoOnly = await people.SsoOnlyAsync(principal, cancellationToken);
        var links = await people.LinksAsync(principal.Id, cancellationToken);
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
            await services.GetRequiredService<LoginThrottles>().LockedUntilAsync(principal.LoginId, cancellationToken),
            factors,
            takeover,
            principal.SsoOnly,
            ssoOnly.SsoOnly && !ssoOnly.Marked,
            [.. links.Select(l => new SsoLinkResponse(l.ProviderId, l.ProviderName, l.Email, l.LinkedAt, l.LastSignInAt))],
            principal.Emergency,
            principal.BreakGlassUntil);
    }

    /// <summary>
    /// The person the request names, when the actor may see them (404 otherwise, as for the record) and holds
    /// <c>identity.users.manage</c> at their home unit (403 otherwise, naming the permission). Tracked, for the change.
    /// </summary>
    internal static async Task<Result<Principal>> ManageableAsync(Guid id, HttpContext http, string doing, CancellationToken cancellationToken, Permission? permission = null)
    {
        permission ??= IdentityPermissions.UsersManage;
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
        var check = new AccessRequest(actor.Id, permission, PermissionFilter.Operation(http), http.Request.Path.Value ?? "/") { Scope = scope };
        var decision = await services.GetRequiredService<IPermissionEvaluator>().EvaluateAsync(check, cancellationToken);
        return decision.Allowed ? principal : Error.Forbidden(PermissionErrors.PermissionDenied, $"You cannot {doing} people in {principal.DisplayName}'s unit.");
    }

    internal static IResult Refused(Error error, HttpContext http, Permission? permission = null)
    {
        var problem = ProblemFactory.Create(error, http);
        if (error.Code == PermissionErrors.PermissionDenied)
        {
            problem.Extensions["permission"] = (permission ?? IdentityPermissions.UsersManage).Name;
        }

        return TypedResults.Problem(problem);
    }

    private static async Task<IResult> UnlockAsync(Guid id, HttpContext http, UserService users, CancellationToken cancellationToken)
    {
        var target = await ManageableAsync(id, http, "unlock", cancellationToken);
        if (target.IsFailure)
        {
            return Refused(target.Error, http);
        }

        var result = await users.UnlockAsync(target.Value, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok(await RecordAsync(result.Value, http.RequestServices, cancellationToken));
    }

    private static async Task<IResult> RecoveryLinkAsync(Guid id, HttpContext http, UserService users, CancellationToken cancellationToken)
    {
        var target = await ManageableAsync(id, http, "issue recovery links for", cancellationToken);
        if (target.IsFailure)
        {
            return Refused(target.Error, http);
        }

        var result = await users.IssueRecoveryAsync(target.Value, cancellationToken);
        return ApiResults.From(result, http, link => TypedResults.Ok(new RecoveryLinkResponse(Links.Recovery(link.Token), link.ExpiresAt)));
    }

    private static async Task<IResult> DeactivateAsync(Guid id, HttpContext http, UserService users, CancellationToken cancellationToken)
    {
        var target = await ManageableAsync(id, http, "deactivate", cancellationToken);
        if (target.IsFailure)
        {
            return Refused(target.Error, http);
        }

        var result = await users.DeactivateAsync(target.Value, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok(await RecordAsync(result.Value, http.RequestServices, cancellationToken));
    }

    private static async Task<IResult> ResetFactorsAsync(Guid id, ResetFactorsRequest request, HttpContext http, UserService users, CancellationToken cancellationToken)
    {
        var target = await ManageableAsync(id, http, "reset the second factors of", cancellationToken);
        if (target.IsFailure)
        {
            return Refused(target.Error, http);
        }

        var result = await users.ResetFactorsAsync(target.Value, request.Reason, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok(await RecordAsync(result.Value, http.RequestServices, cancellationToken));
    }

    private static async Task<IResult> SetSsoOnlyAsync(Guid id, SetSsoOnlyRequest request, HttpContext http, UserService users, CancellationToken cancellationToken)
    {
        var target = await ManageableAsync(id, http, "change how", cancellationToken);
        if (target.IsFailure)
        {
            return Refused(target.Error, http);
        }

        var result = await users.SetSsoOnlyAsync(target.Value, request.SsoOnly, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok(await RecordAsync(result.Value, http.RequestServices, cancellationToken));
    }

    private static async Task<IResult> SetEmergencyAsync(Guid id, SetEmergencyRequest request, HttpContext http, UserService users, CancellationToken cancellationToken)
    {
        var target = await ManageableAsync(id, http, "change the emergency accounts among", cancellationToken);
        if (target.IsFailure)
        {
            return Refused(target.Error, http);
        }

        var result = await users.SetEmergencyAsync(target.Value, request.Emergency, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok(await RecordAsync(result.Value, http.RequestServices, cancellationToken));
    }

    /// <summary>The emergency sign-ins of the last 24 hours (ADR-0049): the banner of every administrator who may manage people.</summary>
    private static async Task<IResult> AlertsAsync(IdentityDbContext context, TimeProvider time, CancellationToken cancellationToken)
    {
        var since = time.GetUtcNow().AddHours(-24);
        var rows = await context.SecurityEvents.AsNoTracking()
            .Where(e => e.Kind == SecurityEventKinds.EmergencySignIn && e.OccurredAt >= since)
            .OrderByDescending(e => e.OccurredAt)
            .Take(20)
            .Join(context.Principals.AsNoTracking(), e => e.PrincipalId, p => p.Id, (e, p) => new { e.Id, PrincipalId = p.Id, p.DisplayName, p.LoginId, e.OccurredAt })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new SecurityAlertsResponse([.. rows.OrderByDescending(r => r.OccurredAt).Select(r => new EmergencySignInResponse(r.Id, r.PrincipalId, r.DisplayName, r.LoginId, r.OccurredAt))]));
    }

    private static async Task<IResult> RevokeAsync(Guid id, RevokeRequest request, HttpContext http, SessionConsole console, CancellationToken cancellationToken)
    {
        var target = await ManageableAsync(id, http, "revoke the access of", cancellationToken);
        if (target.IsFailure)
        {
            return Refused(target.Error, http);
        }

        var result = await console.RevokePrincipalAsync(target.Value, request.Reason, cancellationToken);
        return ApiResults.From(result, http, ended => TypedResults.Ok(new SessionsEndedResponse(ended)));
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
            done.InvitationToken is { } token ? Links.Invitation(token) : null,
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
            new CreateUserResponse(UserResponse.From(created.Principal), Links.Invitation(created.InvitationToken), created.InvitationExpiresAt)));
    }
}
