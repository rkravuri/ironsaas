using FluentValidation;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Identity.Api;

public sealed record LoginRequest(string LoginId, string Password);

public sealed record AcceptInvitationRequest(string Password, string? DisplayName);

public sealed record PrincipalResponse(Guid Id, string LoginId, string DisplayName, PrincipalType Type, PrincipalStatus Status, string? Email, string? Locale, string? TimeZone)
{
    public static PrincipalResponse From(Principal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new PrincipalResponse(principal.Id, principal.LoginId, principal.DisplayName, principal.Type, principal.Status, principal.Email, principal.Locale, principal.TimeZone);
    }
}

/// <summary>
/// A session's times (TF-021): when it started (null for a bearer token), its absolute limit, and when it ends without
/// further activity. My profile shows all three; the shell's warning follows the last two.
/// </summary>
public sealed record SessionResponse(Guid Id, DateTimeOffset? StartedAt, DateTimeOffset ExpiresAt, DateTimeOffset IdleExpiresAt);

public sealed record LoginResponse(PrincipalResponse Principal, SessionResponse Session);

/// <summary>What the shell needs after login: who, what they may do, and when the session ends (TF-021: the expiry warning).</summary>
/// <summary>The home unit as <c>/me</c> shows it: enough to name it; the tree has the rest.</summary>
public sealed record HomeUnitResponse(Guid Id, string Name, string Code);

/// <summary>
/// Who is signed in. <c>permissions</c> are held for the whole tenant; <c>permissions_anywhere</c> at any scope, which
/// is what the shell's navigation shows (ADR-0031). The API still decides per row and per unit.
/// </summary>
public sealed record MeResponse(
    Guid Id,
    string LoginId,
    string DisplayName,
    PrincipalType Type,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> PermissionsAnywhere,
    SessionResponse Session,
    HomeUnitResponse? HomeUnit,
    ActingForResponse? ActingFor,
    MeDelegations Delegations);

/// <summary>A duty by its code and its words.</summary>
public sealed record DutyName(string Code, string Name);

/// <summary>
/// Whom the session acts for (ADR-0037), with the duties and until when: while it is set, <c>permissions</c> and
/// <c>permissions_anywhere</c> are the delegator's for those duties, and every action names both people.
/// </summary>
public sealed record ActingForResponse(Guid Id, string Name, IReadOnlyList<DutyName> Duties, DateTimeOffset Until);

/// <summary>An active delegation as <c>/me</c> shows it: from whom, to whom, which duties, until when.</summary>
public sealed record ActiveDelegationResponse(Guid DelegatorId, string DelegatorName, Guid DelegateId, string DelegateName, IReadOnlyList<DutyName> Duties, DateTimeOffset Until);

/// <summary>The person's active delegations both ways: those they give, and those they hold, which they may act with.</summary>
public sealed record MeDelegations(IReadOnlyList<ActiveDelegationResponse> Given, IReadOnlyList<ActiveDelegationResponse> Received);

public sealed record ActForRequest(Guid PrincipalId);

/// <summary>A person as a picker offers them: enough to tell two people apart, and nothing more.</summary>
public sealed record DirectoryEntry(Guid Id, string DisplayName, string? HomeUnit);

/// <summary>The permissions the signed-in principal holds at one unit or one record, for the shell to show its actions there.</summary>
public sealed record AccessResponse(string Scope, IReadOnlyList<string> Permissions);

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(r => r.LoginId).NotEmpty().MaximumLength(Principal.LoginIdMaxLength);
        RuleFor(r => r.Password).NotEmpty().MaximumLength(1024);
    }
}

public sealed class AcceptInvitationRequestValidator : AbstractValidator<AcceptInvitationRequest>
{
    public AcceptInvitationRequestValidator()
    {
        RuleFor(r => r.Password).NotEmpty().MaximumLength(1024);
        RuleFor(r => r.DisplayName).MaximumLength(Principal.DisplayNameMaxLength);
    }
}

/// <summary>
/// <c>/auth/login</c>, <c>/auth/logout</c> and <c>/auth/invitations/{token}/accept</c> are the anonymous and signed-in
/// doors of a unit (TF-011, TF-018, TF-029); <c>/api/v1/me</c> is what the shell asks first after login.
/// </summary>
internal static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/auth").WithTags("auth");

        auth.MapPost("/login", LoginAsync).Validate<LoginRequest>().AllowAnonymous().WithName("Login").WithSummary("Sign in with login identifier and password.");
        auth.MapPost("/logout", LogoutAsync).RequireSignedIn().WithName("Logout").WithSummary("End the current session.");
        auth.MapPost("/invitations/{token}/accept", AcceptInvitationAsync).Validate<AcceptInvitationRequest>().AllowAnonymous().WithName("AcceptInvitation").WithSummary("Set a password and activate an invited account.");

        endpoints.MapGet("/api/v1/me", MeAsync).RequireSignedIn().WithTags("me").WithName("Me").WithSummary("The signed-in principal, its permissions and the session expiry.");
        endpoints.MapPost("/api/v1/me/acting-for", ActForAsync).RequireSignedIn().WithTags("me").WithName("ActFor").WithSummary("Act for someone who delegated duties to you and whose delegation is active; answers /me.");
        endpoints.MapDelete("/api/v1/me/acting-for", StopActingAsync).RequireSignedIn().WithTags("me").WithName("StopActing").WithSummary("Act as yourself again; answers /me.");
        endpoints.MapGet("/api/v1/directory", DirectoryAsync).RequireSignedIn().WithTags("me").WithName("Directory").WithSummary("Active people of this tenant whose name or login matches the search, for choosing a delegate: name and home unit only.");
        endpoints.MapGet("/api/v1/me/access", AccessAsync).RequireSignedIn().WithTags("me").WithName("MyAccess").WithSummary("Your permissions at a unit (?unit=), over a unit and the units below it (&include_children=true), or at a record (?record_type=&record_id=, with &unit= for the record's unit).");
        TokenEndpoints.Map(endpoints);
        UsersEndpoints.Map(endpoints);
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext http, LoginService login, IdentityOptions options, CancellationToken cancellationToken)
    {
        var result = await login.LoginAsync(request.LoginId, request.Password, cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error, http);
        }

        var outcome = result.Value;
        SessionCookie.Set(http, options, outcome.Token);
        return TypedResults.Ok(new LoginResponse(PrincipalResponse.From(outcome.Principal), Session(outcome.Session.Id, outcome.Session.CreatedAt, outcome.Session.ExpiresAt, outcome.Session.IdleExpiresAt(options.Session.Idle))));
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, RequestPrincipal principal, SessionService sessions, IdentityOptions options, IAuditWriter audit, CancellationToken cancellationToken)
    {
        var current = principal.Current!;
        if (current.Kind == AuthenticationKind.Token)
        {
            return TypedResults.NoContent(); // a token ends with its expiry; revocation is at the principal (TF-029)
        }

        await sessions.EndAsync(current.SessionId, SessionEndReasons.Logout, cancellationToken);
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.Logout, AuditOutcome.Success, current.Id, current.LoginId, RecordType: "session", RecordId: current.SessionId), cancellationToken);
        SessionCookie.Clear(http, options);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> AcceptInvitationAsync(string token, AcceptInvitationRequest request, HttpContext http, InvitationService invitations, CancellationToken cancellationToken)
    {
        var result = await invitations.AcceptAsync(token, request.Password, request.DisplayName, cancellationToken);
        return ApiResults.From(result, http, principal => TypedResults.Ok(PrincipalResponse.From(principal)));
    }

    private static async Task<IResult> MeAsync(HttpContext http, RequestPrincipal principal, CancellationToken cancellationToken) =>
        TypedResults.Ok(await DescribeAsync(http, principal.Current!, cancellationToken));

    /// <summary>
    /// <c>/me</c> for this principal: while acting for someone, the permissions are the delegator's for the delegated duties,
    /// because the grants are (ADR-0037); and the active delegations both ways, with names and duties in words.
    /// </summary>
    private static async Task<MeResponse> DescribeAsync(HttpContext http, SignedInPrincipal current, CancellationToken cancellationToken)
    {
        var services = http.RequestServices;
        var roleAssignments = services.GetRequiredService<IRoleAssignments>();
        var units = services.GetRequiredService<IOrganisationUnits>();
        var delegations = services.GetRequiredService<IDelegations>();
        var principals = services.GetRequiredService<IPrincipals>();
        var duties = services.GetRequiredService<DutyDeclarations>();

        var permissions = await roleAssignments.PermissionsOfAsync(current.Id, cancellationToken);
        var anywhere = await roleAssignments.PermissionsOfAsync(current.Id, AccessScope.Anywhere, cancellationToken);
        var home = current.HomeUnitId is { } unitId ? await units.FindAsync(unitId, cancellationToken) : null;
        var homeUnit = home is null ? null : new HomeUnitResponse(home.Id, home.Name, home.Code);

        var active = await delegations.ActiveOfAsync(current.Id, cancellationToken);
        var names = await principals.FindAsync([.. active.SelectMany(d => new[] { d.DelegatorId, d.DelegateId }).Distinct()], cancellationToken);
        string Name(Guid id) => id == current.Id ? current.DisplayName : names.GetValueOrDefault(id)?.DisplayName ?? id.ToString("D");
        IReadOnlyList<DutyName> Words(IEnumerable<string> codes) => [.. codes.Select(code => new DutyName(code, duties.Find(code)?.Duty.Name ?? code))];
        ActiveDelegationResponse Describe(ActiveDelegation d) => new(d.DelegatorId, Name(d.DelegatorId), d.DelegateId, Name(d.DelegateId), Words(d.Duties), d.Until);

        var received = active.Where(d => d.DelegateId == current.Id).Select(Describe).ToList();
        var given = active.Where(d => d.DelegatorId == current.Id).Select(Describe).ToList();
        var acting = current.ActingForId is { } delegator && received.FirstOrDefault(d => d.DelegatorId == delegator) is { } held
            ? new ActingForResponse(held.DelegatorId, held.DelegatorName, held.Duties, held.Until)
            : null;
        return new MeResponse(current.Id, current.LoginId, current.DisplayName, current.Type, permissions, anywhere, Session(current.SessionId, current.SessionStartedAt, current.SessionExpiresAt, current.IdleExpiresAt), homeUnit, acting, new MeDelegations(given, received));
    }

    private static async Task<IResult> ActForAsync(ActForRequest request, HttpContext http, RequestPrincipal principal, SessionService sessions, CancellationToken cancellationToken)
    {
        var result = await sessions.ActForAsync(principal.Current!, request.PrincipalId, cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error, http);
        }

        // The rest of this request, /me's answer included, acts for the delegator already.
        RequestPrincipal.Set(http, result.Value);
        return TypedResults.Ok(await DescribeAsync(http, result.Value, cancellationToken));
    }

    private static async Task<IResult> StopActingAsync(HttpContext http, RequestPrincipal principal, SessionService sessions, CancellationToken cancellationToken)
    {
        var updated = await sessions.StopActingAsync(principal.Current!, cancellationToken);
        RequestPrincipal.Set(http, updated);
        return TypedResults.Ok(await DescribeAsync(http, updated, cancellationToken));
    }

    /// <summary>
    /// Active people whose name or login identifier contains the search, at most 20, for choosing a delegate: anyone may
    /// delegate, including people who may not list users, so the answer carries a name and a home unit and nothing else.
    /// </summary>
    private static async Task<IResult> DirectoryAsync(string? search, IdentityDbContext context, IOrganisationUnits units, CancellationToken cancellationToken)
    {
        var text = search?.Trim() ?? string.Empty;
        if (text.Length < 2)
        {
            return TypedResults.Ok(new { data = Array.Empty<DirectoryEntry>() });
        }

        var pattern = $"%{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
        var people = await context.Principals.AsNoTracking()
            .Where(p => p.Type == PrincipalType.User && p.Status == PrincipalStatus.Active && (EF.Functions.ILike(p.DisplayName, pattern) || EF.Functions.ILike(p.LoginId, pattern)))
            .OrderBy(p => p.DisplayName)
            .Take(20)
            .Select(p => new { p.Id, p.DisplayName, p.HomeUnitId })
            .ToListAsync(cancellationToken);
        var unitNames = new Dictionary<Guid, string>();
        foreach (var unitId in people.Where(p => p.HomeUnitId is not null).Select(p => p.HomeUnitId!.Value).Distinct())
        {
            if (await units.FindAsync(unitId, cancellationToken) is { } unit)
            {
                unitNames[unitId] = unit.Name;
            }
        }

        return TypedResults.Ok(new { data = people.Select(p => new DirectoryEntry(p.Id, p.DisplayName, p.HomeUnitId is { } id ? unitNames.GetValueOrDefault(id) : null)).ToList() });
    }

    /// <summary>
    /// What the signed-in principal may do at one unit or one record, or over a unit and every unit below it (ADR-0031).
    /// For a record the shell passes the record's unit too, as it shows it; the answer only decides which actions the
    /// shell offers, and every action is checked again by its endpoint.
    /// </summary>
    private static async Task<IResult> AccessAsync(HttpContext http, RequestPrincipal principal, IRoleAssignments roleAssignments, CancellationToken cancellationToken)
    {
        var target = AccessQuery.Parse(http.Request.Query);
        if (target.IsFailure)
        {
            return ApiResults.Problem(target.Error, http);
        }

        var current = principal.Current!.Id;
        if (target.Value.Over is { } over)
        {
            return TypedResults.Ok(new AccessResponse(over.ToString(), await roleAssignments.PermissionsOverAsync(current, over, cancellationToken)));
        }

        return TypedResults.Ok(new AccessResponse(target.Value.Scope.ToString(), await roleAssignments.PermissionsOfAsync(current, target.Value.Scope, cancellationToken)));
    }

    private static SessionResponse Session(Guid id, DateTimeOffset? startedAt, DateTimeOffset expiresAt, DateTimeOffset idleExpiresAt) => new(id, startedAt, expiresAt, idleExpiresAt);
}
