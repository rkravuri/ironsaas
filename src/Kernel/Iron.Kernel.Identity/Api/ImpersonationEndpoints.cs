using FluentValidation;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Iron.Kernel.Identity.Api;

/// <summary>Why and for how long a colleague signs in as a person (TF-025): a reason is required; the duration defaults to the tenant's maximum.</summary>
public sealed record StartImpersonationRequest(string Reason, int? Minutes);

public sealed class StartImpersonationRequestValidator : AbstractValidator<StartImpersonationRequest>
{
    public StartImpersonationRequestValidator()
    {
        RuleFor(r => r.Reason).NotEmpty().WithMessage("Say why you sign in as this person: they and the auditors read it.").MaximumLength(Session.ImpersonationReasonMaxLength);
        RuleFor(r => r.Minutes).InclusiveBetween(1, IdentitySettings.ImpersonationLongestMinutes).When(r => r.Minutes is not null);
    }
}

/// <summary>The impersonation started: whom, until when. The answer sets its cookie; the impersonator's own session stays.</summary>
public sealed record ImpersonationStartResponse(Guid PrincipalId, string DisplayName, DateTimeOffset Until);

/// <summary>Whom someone is signed in as, as <c>/me</c> shows it to them: who they are, until when, why, and whether they are the operator.</summary>
public sealed record ImpersonationResponse(string ImpersonatorName, DateTimeOffset Until, string Reason, bool Operator);

/// <summary>The code of a one-time sign-in link, from its fragment, in the body: never in a path a log keeps (ADR-0046).</summary>
public sealed record SignInLinkRequest(string Code);

public sealed class SignInLinkRequestValidator : AbstractValidator<SignInLinkRequest>
{
    public SignInLinkRequestValidator()
    {
        RuleFor(r => r.Code).NotEmpty().MaximumLength(100);
    }
}

/// <summary>
/// Impersonation (TF-025, ADR-0049): a colleague starts it from the user record, with <c>identity.impersonation.start</c>
/// at the person's unit, a reason and step-up; the impersonator ends it from the band. The operator's one-time link, and
/// a break-glass account's, sign in through <c>/auth/sign-in-link</c>.
/// </summary>
internal static class ImpersonationEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/users/{id:guid}/impersonation", StartAsync).WithTags("users").RequirePermission(IdentityPermissions.ImpersonationStart).ScopedByRow().ForbiddenDuringImpersonation().RequireRecentAuthentication().Validate<StartImpersonationRequest>().Produces<ImpersonationStartResponse>(StatusCodes.Status200OK).WithName("StartImpersonation").WithSummary("Sign in as a person whose permissions you hold, with a reason, for at most the tenant's maximum; your own session stays.");
        endpoints.MapPost("/api/v1/me/impersonation/end", EndAsync).WithTags("me").RequireSignedIn().AllowDuringRequiredStep().Produces(StatusCodes.Status204NoContent).WithName("EndImpersonation").WithSummary("End the impersonation you are in; the next request runs as you again.");
        endpoints.MapPost("/auth/sign-in-link", SignInAsync).WithTags("auth").Validate<SignInLinkRequest>().AllowAnonymous().Produces<LoginResponse>(StatusCodes.Status200OK).WithName("SignInWithLink").WithSummary("Sign in once with a link from the operator's path: an impersonation the tenant allows, or a break-glass account.");
    }

    private static async Task<IResult> StartAsync(Guid id, StartImpersonationRequest request, HttpContext http, ImpersonationService impersonation, IdentityOptions options, CancellationToken cancellationToken)
    {
        var target = await UsersEndpoints.ManageableAsync(id, http, "sign in as", cancellationToken, IdentityPermissions.ImpersonationStart);
        if (target.IsFailure)
        {
            return UsersEndpoints.Refused(target.Error, http, IdentityPermissions.ImpersonationStart);
        }

        var result = await impersonation.StartAsync(target.Value, request.Reason, request.Minutes, cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error, http);
        }

        SessionCookie.Set(http, options.Session.ImpersonationCookieName, result.Value.Token);
        return TypedResults.Ok(new ImpersonationStartResponse(target.Value.Id, target.Value.DisplayName, result.Value.Session.ImpersonationEndsAt!.Value));
    }

    private static async Task<IResult> EndAsync(HttpContext http, ImpersonationService impersonation, IdentityOptions options, CancellationToken cancellationToken)
    {
        var result = await impersonation.EndAsync(cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error, http);
        }

        SessionCookie.Clear(http, options, options.Session.ImpersonationCookieName);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> SignInAsync(SignInLinkRequest request, HttpContext http, ImpersonationService impersonation, IdentityOptions options, CancellationToken cancellationToken)
    {
        var result = await impersonation.SignInWithLinkAsync(request.Code, cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error, http);
        }

        var signedIn = result.Value;
        SessionCookie.Set(http, signedIn.Impersonation ? options.Session.ImpersonationCookieName : options.Session.CookieName, signedIn.Token);
        var session = signedIn.Session;
        return TypedResults.Ok(new LoginResponse(PrincipalResponse.From(signedIn.Principal), new SessionResponse(session.Id, session.CreatedAt, session.ExpiresAt, session.IdleExpiresAt(options.Session.Idle))));
    }
}
