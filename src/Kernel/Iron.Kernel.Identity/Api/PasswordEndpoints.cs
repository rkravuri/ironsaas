using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Api;

/// <summary>The tenant's password rules, for the shell to say in words before anyone types (TF-012).</summary>
public sealed record PasswordPolicyResponse(int MinimumLength, int MaximumLength, bool CommonCheck, int HistoryDepth, int MaximumAgeDays, int WarningDays)
{
    public static PasswordPolicyResponse From(PasswordRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new PasswordPolicyResponse(rules.MinimumLength, rules.MaximumLength, rules.CommonCheck, rules.HistoryDepth, rules.MaximumAgeDays, PasswordRules.WarningDays);
    }
}

/// <summary>The link's code comes in the body, from its fragment, never in a path a log or a trace keeps (ADR-0046).</summary>
public sealed record RecoverPasswordRequest(string Token, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword, bool? EndOtherSessions);

/// <summary>One of the person's security events as My profile lists it: what, when, by whom, and why.</summary>
public sealed record SecurityEventResponse(Guid Id, string Kind, DateTimeOffset OccurredAt, string Actor, string? ActorName, string? Reason);

public sealed class RecoverPasswordRequestValidator : AbstractValidator<RecoverPasswordRequest>
{
    public RecoverPasswordRequestValidator()
    {
        RuleFor(r => r.Token).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Password).NotEmpty().MaximumLength(1024);
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(r => r.CurrentPassword).NotEmpty().MaximumLength(1024);
        RuleFor(r => r.NewPassword).NotEmpty().MaximumLength(1024);
    }
}

/// <summary>
/// Passwords and security activity (TF-012, TF-017, TF-019, ADR-0046): the policy in words and the recovery link are
/// anonymous doors, since the person has no session yet; changing a password and reading one's own security activity
/// need a signed-in person and no permission, because both are about oneself.
/// </summary>
internal static class PasswordEndpoints
{
    public static readonly ListFields<SecurityEvent> SecurityEventFields = new ListFields<SecurityEvent>()
        .Add("id", e => e.Id, FieldUse.Select | FieldUse.Filter)
        .Add("kind", e => e.Kind, FieldUse.Select | FieldUse.Filter)
        .Add("occurred_at", e => e.OccurredAt)
        .Add("actor", e => e.Actor, FieldUse.Select | FieldUse.Filter)
        .Add("actor_name", e => e.ActorName, FieldUse.Select)
        .Add("reason", e => e.Reason, FieldUse.Select)
        .SortByDefault("occurred_at", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/auth").WithTags("auth");
        auth.MapGet("/password-policy", PolicyAsync).AllowAnonymous().Produces<PasswordPolicyResponse>(StatusCodes.Status200OK).WithName("PasswordPolicy").WithSummary("The tenant's password rules, for the forms that set a password.");
        auth.MapPost("/recovery", RecoverAsync).Validate<RecoverPasswordRequest>().AllowAnonymous().Produces<PrincipalResponse>(StatusCodes.Status200OK).WithName("RecoverPassword").WithSummary("Set a new password with a recovery link: one use, 15 minutes; every session of the person ends.");

        endpoints.MapPost("/api/v1/me/password", ChangeAsync).Validate<ChangePasswordRequest>().RequireSignedIn().ForbiddenDuringImpersonation().WithTags("me").Produces(StatusCodes.Status204NoContent).WithName("ChangePassword").WithSummary("Change your password with the current one; end your other sessions unless you say not to.");
        endpoints.MapGet("/api/v1/me/security-events", SecurityEventsAsync).RequireSignedIn().WithTags("me").Produces<ListResponse<SecurityEventResponse>>(StatusCodes.Status200OK).WithName("MySecurityEvents").WithSummary("Your security activity, newest first: password changes, recovery links, lockouts, deactivation.");
    }

    private static async Task<IResult> PolicyAsync(PasswordPolicy policy, CancellationToken cancellationToken) =>
        TypedResults.Ok(PasswordPolicyResponse.From(await policy.RulesAsync(cancellationToken)));

    private static async Task<IResult> RecoverAsync(RecoverPasswordRequest request, HttpContext http, PasswordService passwords, CancellationToken cancellationToken)
    {
        var result = await passwords.RecoverAsync(request.Token, request.Password, cancellationToken);
        return ApiResults.From(result, http, principal => TypedResults.Ok(PrincipalResponse.From(principal)));
    }

    private static async Task<IResult> ChangeAsync(ChangePasswordRequest request, HttpContext http, RequestPrincipal principal, PasswordService passwords, CancellationToken cancellationToken)
    {
        var current = principal.Current!;
        Guid? keep = current.Kind == AuthenticationKind.Session ? current.SessionId : null;
        var result = await passwords.ChangeAsync(current.Id, keep, request.CurrentPassword, request.NewPassword, request.EndOtherSessions ?? true, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.NoContent();
    }

    private static async Task<IResult> SecurityEventsAsync(HttpContext http, RequestPrincipal principal, IdentityDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, SecurityEventFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var mine = context.SecurityEvents.AsNoTracking().Where(e => e.PrincipalId == principal.Current!.Id);
        var page = await mine.ApplyListQuery(query, SecurityEventFields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(e => new SecurityEventResponse(e.Id, e.Kind, e.OccurredAt, e.Actor, e.ActorName, e.Reason)), http, query);
    }
}
