using FluentValidation;
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
        return TypedResults.Ok(new LoginResponse(PrincipalResponse.From(outcome.Principal), Session(outcome.Session.Id, outcome.Session.ExpiresAt, outcome.Session.IdleExpiresAt(options.Session.Idle))));
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

    private static async Task<IResult> MeAsync(RequestPrincipal principal, IRoleAssignments roleAssignments, CancellationToken cancellationToken)
    {
        var current = principal.Current!;
        var permissions = await roleAssignments.PermissionsOfAsync(current.Id, cancellationToken);
        return TypedResults.Ok(new MeResponse(current.Id, current.LoginId, current.DisplayName, current.Type, permissions, Session(current.SessionId, current.SessionExpiresAt, current.IdleExpiresAt)));
    }

    private static SessionResponse Session(Guid id, DateTimeOffset expiresAt, DateTimeOffset idleExpiresAt) => new(id, expiresAt, idleExpiresAt);
}
