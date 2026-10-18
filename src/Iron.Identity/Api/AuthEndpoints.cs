using FluentValidation;
using Iron.Api.Permissions;
using Iron.Api.Responses;
using Iron.Api.Validation;
using Iron.Authorization.Contracts;
using Iron.Audit.Contracts;
using Iron.Identity.Contracts;
using Iron.Identity.Data;
using Iron.Identity.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Iron.Identity.Api;

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

public sealed record SessionResponse(Guid Id, DateTimeOffset ExpiresAt, DateTimeOffset IdleExpiresAt);

public sealed record LoginResponse(PrincipalResponse Principal, SessionResponse Session);

/// <summary>What the shell needs after login: who, what they may do, and when the session ends (TF-021: the expiry warning).</summary>
public sealed record MeResponse(Guid Id, string LoginId, string DisplayName, PrincipalType Type, IReadOnlyList<string> Permissions, SessionResponse Session);

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
        return TypedResults.Ok(new LoginResponse(PrincipalResponse.From(outcome.Principal), Session(outcome.Session.Id, outcome.Session.ExpiresAt, outcome.Session.IdleExpiresAt(options.Session.Idle))));
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, RequestPrincipal principal, SessionService sessions, IdentityOptions options, IAuditWriter audit, CancellationToken cancellationToken)
    {
        var current = principal.Current!;
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

    private static async Task<IResult> MeAsync(RequestPrincipal principal, IRoleAssignments roleAssignments, CancellationToken cancellationToken)
    {
        var current = principal.Current!;
        var permissions = await roleAssignments.PermissionsOfAsync(current.Id, cancellationToken);
        return TypedResults.Ok(new MeResponse(current.Id, current.LoginId, current.DisplayName, current.Type, permissions, Session(current.SessionId, current.SessionExpiresAt, current.IdleExpiresAt)));
    }

    private static SessionResponse Session(Guid id, DateTimeOffset expiresAt, DateTimeOffset idleExpiresAt) => new(id, expiresAt, idleExpiresAt);
}
