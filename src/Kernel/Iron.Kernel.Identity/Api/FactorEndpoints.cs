using System.Text.Json;
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
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Api;

/// <summary>The second step of a sign-in: a code from the app, a recovery code, or a passkey's answer as the browser gives it.</summary>
public sealed record SecondFactorRequest(string? Code, string? RecoveryCode, JsonElement? Passkey)
{
    public SecondFactorProof Proof => new(Code, RecoveryCode, Passkey);
}

/// <summary>What a sign-in goes on to after a right password: <c>second_factor</c> or <c>enrolment</c>, the ways open, and until when.</summary>
public sealed record SignInStepResponse(string Kind, IReadOnlyList<string> Methods, DateTimeOffset ExpiresAt);

/// <summary>The options of a WebAuthn ceremony, as the browser's API takes them (base64url for the bytes).</summary>
public sealed record WebAuthnOptionsResponse(JsonElement Options);

/// <summary>An authenticator app waiting for its first code: the secret to type and the URI the QR code shows.</summary>
public sealed record AuthenticatorAppSetupResponse(string Secret, string Uri, DateTimeOffset ExpiresAt);

public sealed record ConfirmAuthenticatorAppRequest(string Code, string? Name);

/// <summary>A new passkey: the name the person chose and the authenticator's answer as the browser gives it.</summary>
public sealed record RegisterPasskeyRequest(string? Name, JsonElement Credential);

public sealed record FactorResponse(Guid Id, string Kind, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt)
{
    public static FactorResponse From(Factor factor)
    {
        ArgumentNullException.ThrowIfNull(factor);
        return new FactorResponse(factor.Id, factor.Kind, factor.Name, factor.CreatedAt, factor.LastUsedAt);
    }
}

/// <summary>My profile's factors: each one, the recovery codes left, whether the policy requires a factor of the person, what they may enrol, and the end of their grace.</summary>
public sealed record FactorsResponse(IReadOnlyList<FactorResponse> Factors, int RecoveryCodesLeft, bool Required, IReadOnlyList<string> Allowed, DateTimeOffset? EnrolBy);

/// <summary>A factor enrolled, with the recovery codes when it was the first: shown once.</summary>
public sealed record EnrolledResponse(FactorResponse Factor, IReadOnlyList<string>? RecoveryCodes);

public sealed record RenameFactorRequest(string Name);

public sealed record RecoveryCodesResponse(IReadOnlyList<string> Codes);

/// <summary>
/// What confirming it is them asks of the person: the password, and the second factor when they have one. A person with no
/// password confirms by signing in again through their provider (ADR-0048): <c>password</c> is false and <c>provider</c> names it.
/// </summary>
public sealed record ReauthenticationOptionsResponse(bool SecondFactor, IReadOnlyList<string> Methods, JsonElement? PasskeyOptions, bool Password = true, SsoProviderButton? Provider = null);

public sealed record ReauthenticateRequest(string Password, string? Code, string? RecoveryCode, JsonElement? Passkey)
{
    public SecondFactorProof Proof => new(Code, RecoveryCode, Passkey);
}

public sealed record ReauthenticatedResponse(DateTimeOffset AuthenticatedAt);

/// <summary>
/// Confirming it is you through your company login (TF-248 AC6, ADR-0048): where to come back to, and your second factor
/// when you have one here, given before you leave.
/// </summary>
public sealed record SsoReauthenticationRequest(string? ReturnTo, string? Code, string? RecoveryCode, JsonElement? Passkey)
{
    public SecondFactorProof Proof => new(Code, RecoveryCode, Passkey);
}

/// <summary>Whether the login page offers a sign-in with a passkey alone (ADR-0047), and the providers it offers a button for (ADR-0048).</summary>
public sealed record SignInOptionsResponse(bool PasskeySignIn, IReadOnlyList<SsoProviderButton> Providers);

public sealed record PasskeySignInRequest(JsonElement Credential);

public sealed class SecondFactorRequestValidator : AbstractValidator<SecondFactorRequest>
{
    public SecondFactorRequestValidator()
    {
        RuleFor(r => r.Code).MaximumLength(FieldLimits.OneTimeCode);
        RuleFor(r => r.RecoveryCode).MaximumLength(FieldLimits.RecoveryCode);
        RuleFor(r => r.Passkey).MaximumJsonSize(FieldLimits.PasskeyBytes);
        RuleFor(r => r).Must(r => !r.Proof.IsEmpty).WithName("code").WithMessage("Type the code from your app or a recovery code, or use your passkey.");
    }
}

public sealed class ConfirmAuthenticatorAppRequestValidator : AbstractValidator<ConfirmAuthenticatorAppRequest>
{
    public ConfirmAuthenticatorAppRequestValidator()
    {
        RuleFor(r => r.Code).NotEmpty().MaximumLength(20);
        RuleFor(r => r.Name).MaximumLength(Factor.NameMaxLength);
    }
}

public sealed class RegisterPasskeyRequestValidator : AbstractValidator<RegisterPasskeyRequest>
{
    public RegisterPasskeyRequestValidator()
    {
        RuleFor(r => r.Name).MaximumLength(Factor.NameMaxLength);
        RuleFor(r => r.Credential).Must(c => c.ValueKind == JsonValueKind.Object).WithMessage("The passkey's answer is missing.").MaximumJsonSize(FieldLimits.PasskeyBytes);
    }
}

public sealed class RenameFactorRequestValidator : AbstractValidator<RenameFactorRequest>
{
    public RenameFactorRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(Factor.NameMaxLength);
    }
}

public sealed class ReauthenticateRequestValidator : AbstractValidator<ReauthenticateRequest>
{
    public ReauthenticateRequestValidator()
    {
        RuleFor(r => r.Password).NotEmpty().MaximumLength(FieldLimits.Password);
        RuleFor(r => r.Code).MaximumLength(FieldLimits.OneTimeCode);
        RuleFor(r => r.RecoveryCode).MaximumLength(FieldLimits.RecoveryCode);
        RuleFor(r => r.Passkey).MaximumJsonSize(FieldLimits.PasskeyBytes);
    }
}

public sealed class SsoReauthenticationRequestValidator : AbstractValidator<SsoReauthenticationRequest>
{
    public SsoReauthenticationRequestValidator()
    {
        RuleFor(r => r.ReturnTo).MaximumLength(FieldLimits.ReturnPath);
        RuleFor(r => r.Code).MaximumLength(FieldLimits.OneTimeCode);
        RuleFor(r => r.RecoveryCode).MaximumLength(FieldLimits.RecoveryCode);
        RuleFor(r => r.Passkey).MaximumJsonSize(FieldLimits.PasskeyBytes);
    }
}

public sealed class PasskeySignInRequestValidator : AbstractValidator<PasskeySignInRequest>
{
    public PasskeySignInRequestValidator()
    {
        RuleFor(r => r.Credential).Must(c => c.ValueKind == JsonValueKind.Object).WithMessage("The passkey's answer is missing.").MaximumJsonSize(FieldLimits.PasskeyBytes);
    }
}

/// <summary>
/// Second factors (TF-013, TF-248, ADR-0047). The sign-in's doors are anonymous and hold their state in the sign-in
/// cookie: the second step, enrolment when the policy requires a factor, and a sign-in with a passkey alone where the
/// tenant allows it. A signed-in person manages their own factors in My profile, adding, renaming and removing behind a
/// recent authentication, and confirms it is them when a high-risk action asks.
/// </summary>
internal static class FactorEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/auth").WithTags("auth");
        auth.MapPost("/second-factor", SecondFactorAsync).Validate<SecondFactorRequest>().AllowAnonymous().Produces<LoginResponse>(StatusCodes.Status200OK).WithName("SecondFactor").WithSummary("The second step of a sign-in: a code from your app, a recovery code, or your passkey.");
        auth.MapPost("/second-factor/passkey-options", SecondFactorPasskeyOptionsAsync).AllowAnonymous().Produces<WebAuthnOptionsResponse>(StatusCodes.Status200OK).WithName("SecondFactorPasskeyOptions").WithSummary("The options for your passkey at the second step of a sign-in.");
        auth.MapGet("/sign-in-options", SignInOptionsAsync).AllowAnonymous().Produces<SignInOptionsResponse>(StatusCodes.Status200OK).WithName("SignInOptions").WithSummary("Whether this tenant lets people sign in with a passkey alone, and its company logins.");
        auth.MapPost("/passkey/options", PasskeySignInOptionsAsync).AllowAnonymous().Produces<WebAuthnOptionsResponse>(StatusCodes.Status200OK).WithName("PasskeySignInOptions").WithSummary("The options for a sign-in with a passkey alone, where the tenant allows it.");
        auth.MapPost("/passkey", PasskeySignInAsync).Validate<PasskeySignInRequest>().AllowAnonymous().Produces<LoginResponse>(StatusCodes.Status200OK).WithName("PasskeySignIn").WithSummary("Sign in with a passkey alone, where the tenant allows it.");
        auth.MapPost("/enrolment/authenticator-app", EnrolmentAppAsync).AllowAnonymous().Produces<AuthenticatorAppSetupResponse>(StatusCodes.Status200OK).WithName("EnrolmentAuthenticatorApp").WithSummary("At sign-in, when you must have a second factor: start an authenticator app.");
        auth.MapPost("/enrolment/authenticator-app/confirm", EnrolmentAppConfirmAsync).Validate<ConfirmAuthenticatorAppRequest>().AllowAnonymous().Produces<LoginResponse>(StatusCodes.Status200OK).WithName("EnrolmentAuthenticatorAppConfirm").WithSummary("At sign-in: the app's first code saves it and signs you in; the answer shows your recovery codes once.");
        auth.MapPost("/enrolment/passkey/options", EnrolmentPasskeyOptionsAsync).AllowAnonymous().Produces<WebAuthnOptionsResponse>(StatusCodes.Status200OK).WithName("EnrolmentPasskeyOptions").WithSummary("At sign-in, when you must have a second factor: the options to create a passkey.");
        auth.MapPost("/enrolment/passkey", EnrolmentPasskeyAsync).Validate<RegisterPasskeyRequest>().AllowAnonymous().Produces<LoginResponse>(StatusCodes.Status200OK).WithName("EnrolmentPasskey").WithSummary("At sign-in: the new passkey is saved and signs you in; the answer shows your recovery codes once.");

        var me = endpoints.MapGroup("/api/v1/me").WithTags("me");
        // The enrolment's own doors answer while the policy requires a factor the person has not got (ADR-0047).
        me.MapGet("/factors", FactorsAsync).RequireSignedIn().AllowDuringRequiredStep(IdentityErrors.MfaEnrolmentRequired).Produces<FactorsResponse>(StatusCodes.Status200OK).WithName("MyFactors").WithSummary("Your second factors, the recovery codes left, and what your organisation's policy asks of you.");
        me.MapPost("/factors/authenticator-app", AddAppAsync).RequireSignedIn().AllowDuringRequiredStep(IdentityErrors.MfaEnrolmentRequired).ForbiddenDuringImpersonation().RequireRecentAuthentication().Produces<AuthenticatorAppSetupResponse>(StatusCodes.Status200OK).WithName("AddAuthenticatorApp").WithSummary("Start an authenticator app: the secret and its QR code; the first code saves it.");
        me.MapPost("/factors/authenticator-app/confirm", ConfirmAppAsync).Validate<ConfirmAuthenticatorAppRequest>().RequireSignedIn().AllowDuringRequiredStep(IdentityErrors.MfaEnrolmentRequired).ForbiddenDuringImpersonation().Produces<EnrolledResponse>(StatusCodes.Status200OK).WithName("ConfirmAuthenticatorApp").WithSummary("The app's first code saves it; your first factor brings recovery codes, shown once.");
        me.MapPost("/factors/passkey/options", AddPasskeyOptionsAsync).RequireSignedIn().AllowDuringRequiredStep(IdentityErrors.MfaEnrolmentRequired).ForbiddenDuringImpersonation().RequireRecentAuthentication().Produces<WebAuthnOptionsResponse>(StatusCodes.Status200OK).WithName("AddPasskeyOptions").WithSummary("The options to create a passkey.");
        me.MapPost("/factors/passkey", AddPasskeyAsync).Validate<RegisterPasskeyRequest>().RequireSignedIn().AllowDuringRequiredStep(IdentityErrors.MfaEnrolmentRequired).ForbiddenDuringImpersonation().Produces<EnrolledResponse>(StatusCodes.Status200OK).WithName("AddPasskey").WithSummary("Save the new passkey with its name; your first factor brings recovery codes, shown once.");
        me.MapPatch("/factors/{id:guid}", RenameAsync).Validate<RenameFactorRequest>().RequireSignedIn().ForbiddenDuringImpersonation().RequireRecentAuthentication().Produces<FactorResponse>(StatusCodes.Status200OK).WithName("RenameFactor").WithSummary("Rename one of your factors.");
        me.MapDelete("/factors/{id:guid}", RemoveAsync).RequireSignedIn().ForbiddenDuringImpersonation().RequireRecentAuthentication().Produces(StatusCodes.Status204NoContent).WithName("RemoveFactor").WithSummary("Remove one of your factors; the last one stays while your organisation requires one.");
        me.MapPost("/recovery-codes", RegenerateAsync).RequireSignedIn().ForbiddenDuringImpersonation().RequireRecentAuthentication().Produces<RecoveryCodesResponse>(StatusCodes.Status200OK).WithName("RegenerateRecoveryCodes").WithSummary("Ten new recovery codes, shown once; the old ones stop working.");
        me.MapPost("/reauthentication", ReauthenticationAsync).RequireSignedIn().AllowDuringRequiredStep(IdentityErrors.MfaEnrolmentRequired).ForbiddenDuringImpersonation().Produces<ReauthenticationOptionsResponse>(StatusCodes.Status200OK).WithName("ReauthenticationOptions").WithSummary("What confirming it is you asks: your password, and your second factor when you have one.");
        me.MapPost("/reauthentication/sso", ReauthenticateThroughProviderAsync).Validate<SsoReauthenticationRequest>().RequireSignedIn().AllowDuringRequiredStep(IdentityErrors.MfaEnrolmentRequired).ForbiddenDuringImpersonation().Produces<SsoStartResponse>(StatusCodes.Status200OK).WithName("ReauthenticateThroughProvider").WithSummary("Confirm it is you through your company login, with a fresh sign-in there; your second factor first when you have one here.");
        me.MapPost("/reauthenticate", ReauthenticateAsync).Validate<ReauthenticateRequest>().RequireSignedIn().AllowDuringRequiredStep(IdentityErrors.MfaEnrolmentRequired).ForbiddenDuringImpersonation().Produces<ReauthenticatedResponse>(StatusCodes.Status200OK).WithName("Reauthenticate").WithSummary("Confirm it is you, for a high-risk action: your password, and your second factor when you have one.");
    }

    private static async Task<IResult> SecondFactorAsync(SecondFactorRequest request, HttpContext http, LoginService login, IdentityOptions options, CancellationToken cancellationToken) =>
        SignIn(await login.SecondFactorAsync(http, request.Proof, cancellationToken), http, options);

    private static async Task<IResult> SecondFactorPasskeyOptionsAsync(HttpContext http, LoginService login, CancellationToken cancellationToken) =>
        ApiResults.From(await login.SecondFactorPasskeyOptionsAsync(http, cancellationToken), http, json => TypedResults.Ok(Options(json)));

    private static async Task<IResult> SignInOptionsAsync(MfaPolicy policy, SsoSignInService sso, CancellationToken cancellationToken) =>
        TypedResults.Ok(new SignInOptionsResponse((await policy.RulesAsync(cancellationToken)).PasskeySignIn, [.. (await sso.ProvidersAsync(cancellationToken)).Select(SsoProviderButton.From)]));

    private static async Task<IResult> PasskeySignInOptionsAsync(HttpContext http, LoginService login, CancellationToken cancellationToken)
    {
        var result = await login.PasskeySignInOptionsAsync(cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error, http);
        }

        AuthenticationChallenges.SetCookie(http, result.Value.Token, ChallengePurposes.PasskeySignIn);
        return TypedResults.Ok(Options(result.Value.Options));
    }

    private static async Task<IResult> PasskeySignInAsync(PasskeySignInRequest request, HttpContext http, LoginService login, IdentityOptions options, CancellationToken cancellationToken) =>
        SignIn(await login.PasskeySignInAsync(http, request.Credential, cancellationToken), http, options);

    private static async Task<IResult> EnrolmentAppAsync(HttpContext http, LoginService login, FactorService factors, CancellationToken cancellationToken)
    {
        var pending = await login.EnrolmentAsync(http, cancellationToken);
        if (pending.IsFailure)
        {
            return ApiResults.Problem(pending.Error, http);
        }

        var setup = await factors.BeginAuthenticatorAppAsync(pending.Value.Challenge, pending.Value.Principal, cancellationToken);
        return ApiResults.From(setup, http, s => TypedResults.Ok(new AuthenticatorAppSetupResponse(s.Secret, s.Uri, s.ExpiresAt)));
    }

    private static async Task<IResult> EnrolmentAppConfirmAsync(ConfirmAuthenticatorAppRequest request, HttpContext http, LoginService login, FactorService factors, IdentityOptions options, CancellationToken cancellationToken)
    {
        var pending = await login.EnrolmentAsync(http, cancellationToken);
        if (pending.IsFailure)
        {
            return ApiResults.Problem(pending.Error, http);
        }

        var (challenge, principal) = pending.Value;
        var enrolled = await factors.ConfirmAuthenticatorAppAsync(challenge, principal, request.Code, request.Name, cancellationToken);
        return enrolled.IsFailure ? ApiResults.Problem(enrolled.Error, http) : SignIn(await login.EnrolledAsync(challenge, principal, enrolled.Value, cancellationToken), http, options);
    }

    private static async Task<IResult> EnrolmentPasskeyOptionsAsync(HttpContext http, LoginService login, FactorService factors, CancellationToken cancellationToken)
    {
        var pending = await login.EnrolmentAsync(http, cancellationToken);
        if (pending.IsFailure)
        {
            return ApiResults.Problem(pending.Error, http);
        }

        return ApiResults.From(await factors.BeginPasskeyAsync(pending.Value.Challenge, pending.Value.Principal, cancellationToken), http, json => TypedResults.Ok(Options(json)));
    }

    private static async Task<IResult> EnrolmentPasskeyAsync(RegisterPasskeyRequest request, HttpContext http, LoginService login, FactorService factors, IdentityOptions options, CancellationToken cancellationToken)
    {
        var pending = await login.EnrolmentAsync(http, cancellationToken);
        if (pending.IsFailure)
        {
            return ApiResults.Problem(pending.Error, http);
        }

        var (challenge, principal) = pending.Value;
        var enrolled = await factors.FinishPasskeyAsync(challenge, principal, request.Name, request.Credential, cancellationToken);
        return enrolled.IsFailure ? ApiResults.Problem(enrolled.Error, http) : SignIn(await login.EnrolledAsync(challenge, principal, enrolled.Value, cancellationToken), http, options);
    }

    private static async Task<IResult> FactorsAsync(HttpContext http, RequestPrincipal principal, IdentityDbContext context, FactorService factors, CancellationToken cancellationToken)
    {
        var person = await PersonAsync(principal, context, cancellationToken);
        if (person.IsFailure)
        {
            return ApiResults.Problem(person.Error, http);
        }

        var overview = await factors.OverviewAsync(person.Value, cancellationToken);
        return TypedResults.Ok(new FactorsResponse([.. overview.Factors.Select(FactorResponse.From)], overview.RecoveryCodesLeft, overview.Required, overview.Allowed, overview.EnrolBy));
    }

    private static async Task<IResult> AddAppAsync(HttpContext http, RequestPrincipal principal, IdentityDbContext context, AuthenticationChallenges challenges, FactorService factors, CancellationToken cancellationToken)
    {
        var person = await PersonAsync(principal, context, cancellationToken);
        if (person.IsFailure)
        {
            return ApiResults.Problem(person.Error, http);
        }

        var challenge = await challenges.StartForSessionAsync(ChallengePurposes.AddFactor, person.Value.Id, principal.Current!.SessionId, cancellationToken);
        var setup = await factors.BeginAuthenticatorAppAsync(challenge, person.Value, cancellationToken);
        return ApiResults.From(setup, http, s => TypedResults.Ok(new AuthenticatorAppSetupResponse(s.Secret, s.Uri, s.ExpiresAt)));
    }

    private static async Task<IResult> ConfirmAppAsync(ConfirmAuthenticatorAppRequest request, HttpContext http, RequestPrincipal principal, IdentityDbContext context, AuthenticationChallenges challenges, FactorService factors, CancellationToken cancellationToken)
    {
        var pending = await AddingAsync(principal, context, challenges, cancellationToken);
        if (pending.IsFailure)
        {
            return ApiResults.Problem(pending.Error, http);
        }

        var enrolled = await factors.ConfirmAuthenticatorAppAsync(pending.Value.Challenge, pending.Value.Person, request.Code, request.Name, cancellationToken);
        return ApiResults.From(enrolled, http, e => TypedResults.Ok(new EnrolledResponse(FactorResponse.From(e.Factor), e.RecoveryCodes)));
    }

    private static async Task<IResult> AddPasskeyOptionsAsync(HttpContext http, RequestPrincipal principal, IdentityDbContext context, AuthenticationChallenges challenges, FactorService factors, CancellationToken cancellationToken)
    {
        var person = await PersonAsync(principal, context, cancellationToken);
        if (person.IsFailure)
        {
            return ApiResults.Problem(person.Error, http);
        }

        var challenge = await challenges.StartForSessionAsync(ChallengePurposes.AddFactor, person.Value.Id, principal.Current!.SessionId, cancellationToken);
        return ApiResults.From(await factors.BeginPasskeyAsync(challenge, person.Value, cancellationToken), http, json => TypedResults.Ok(Options(json)));
    }

    private static async Task<IResult> AddPasskeyAsync(RegisterPasskeyRequest request, HttpContext http, RequestPrincipal principal, IdentityDbContext context, AuthenticationChallenges challenges, FactorService factors, CancellationToken cancellationToken)
    {
        var pending = await AddingAsync(principal, context, challenges, cancellationToken);
        if (pending.IsFailure)
        {
            return ApiResults.Problem(pending.Error, http);
        }

        var enrolled = await factors.FinishPasskeyAsync(pending.Value.Challenge, pending.Value.Person, request.Name, request.Credential, cancellationToken);
        return ApiResults.From(enrolled, http, e => TypedResults.Ok(new EnrolledResponse(FactorResponse.From(e.Factor), e.RecoveryCodes)));
    }

    private static async Task<IResult> RenameAsync(Guid id, RenameFactorRequest request, HttpContext http, RequestPrincipal principal, IdentityDbContext context, FactorService factors, CancellationToken cancellationToken)
    {
        var person = await PersonAsync(principal, context, cancellationToken);
        if (person.IsFailure)
        {
            return ApiResults.Problem(person.Error, http);
        }

        return ApiResults.From(await factors.RenameAsync(person.Value, id, request.Name, cancellationToken), http, factor => TypedResults.Ok(FactorResponse.From(factor)));
    }

    private static async Task<IResult> RemoveAsync(Guid id, HttpContext http, RequestPrincipal principal, IdentityDbContext context, FactorService factors, CancellationToken cancellationToken)
    {
        var person = await PersonAsync(principal, context, cancellationToken);
        if (person.IsFailure)
        {
            return ApiResults.Problem(person.Error, http);
        }

        return ApiResults.From(await factors.RemoveAsync(person.Value, id, cancellationToken), http, () => TypedResults.NoContent());
    }

    private static async Task<IResult> RegenerateAsync(HttpContext http, RequestPrincipal principal, IdentityDbContext context, FactorService factors, CancellationToken cancellationToken)
    {
        var person = await PersonAsync(principal, context, cancellationToken);
        if (person.IsFailure)
        {
            return ApiResults.Problem(person.Error, http);
        }

        return ApiResults.From(await factors.RegenerateRecoveryCodesAsync(person.Value, cancellationToken), http, codes => TypedResults.Ok(new RecoveryCodesResponse(codes)));
    }

    private static async Task<IResult> ReauthenticationAsync(HttpContext http, RequestPrincipal principal, ReauthenticationService reauthentication, CancellationToken cancellationToken)
    {
        var result = await reauthentication.ChallengeAsync(principal.Current!, cancellationToken);
        return ApiResults.From(result, http, r => TypedResults.Ok(new ReauthenticationOptionsResponse(r.SecondFactor, r.Methods, r.PasskeyOptions is { } json ? Parse(json) : null, r.Password, r.Provider is { } provider ? SsoProviderButton.From(provider) : null)));
    }

    private static async Task<IResult> ReauthenticateAsync(ReauthenticateRequest request, HttpContext http, RequestPrincipal principal, ReauthenticationService reauthentication, CancellationToken cancellationToken)
    {
        var result = await reauthentication.ReauthenticateAsync(principal.Current!, request.Password, request.Proof, cancellationToken);
        return ApiResults.From(result, http, at => TypedResults.Ok(new ReauthenticatedResponse(at)));
    }

    private static async Task<IResult> ReauthenticateThroughProviderAsync(SsoReauthenticationRequest request, HttpContext http, RequestPrincipal principal, SsoSignInService sso, CancellationToken cancellationToken)
    {
        var result = await sso.StartReauthenticationAsync(principal.Current!, SsoEndpoints.RedirectUri(http), request.ReturnTo, request.Proof, cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error, http);
        }

        AuthenticationChallenges.SetCookie(http, result.Value.ChallengeToken, ChallengePurposes.SsoReauthentication);
        return TypedResults.Ok(new SsoStartResponse(result.Value.AuthorizationUrl.AbsoluteUri, result.Value.ExpiresAt));
    }

    /// <summary>
    /// The answer of a sign-in step: the next step with its cookie, or the session with its cookie, the sign-in cookie
    /// cleared. A password past its age is the problem with the recovery link's code, as at the password step.
    /// </summary>
    internal static IResult SignIn(Result<LoginOutcome> result, HttpContext http, IdentityOptions options)
    {
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error, http);
        }

        var outcome = result.Value;
        if (outcome.Step is { } step)
        {
            AuthenticationChallenges.SetCookie(http, step.ChallengeToken, step.Kind);
            return TypedResults.Ok(new LoginResponse(null, null, new SignInStepResponse(step.Kind, step.Methods, step.ExpiresAt), ReturnTo: outcome.ReturnTo));
        }

        if (http.Request.Cookies.ContainsKey(AuthenticationChallenges.CookieName))
        {
            AuthenticationChallenges.ClearCookie(http);
        }

        if (outcome.ReauthenticatedAt is not null)
        {
            // The session's person confirmed it is them through their provider: the session stays as it was, more recent.
            return TypedResults.Ok(new LoginResponse(null, null, ReturnTo: outcome.ReturnTo, Reauthenticated: true));
        }

        if (outcome.RecoveryToken is { } recovery)
        {
            // The password was right but is too old (TF-012): no session, and the code of a recovery link to set a new one.
            var problem = Iron.Kernel.Api.Problems.ProblemFactory.Create(Error.Unauthorized(IdentityErrors.PasswordExpired, LoginService.ExpiredMessage), http);
            problem.Extensions["recovery_token"] = recovery;
            return TypedResults.Problem(problem);
        }

        SessionCookie.Set(http, options, outcome.Token!);
        var session = outcome.Session!;
        return TypedResults.Ok(new LoginResponse(
            PrincipalResponse.From(outcome.Principal),
            new SessionResponse(session.Id, session.CreatedAt, session.ExpiresAt, session.IdleExpiresAt(options.Session.Idle)),
            null,
            outcome.EnrolNewFactor,
            outcome.RecoveryCodes,
            outcome.ReturnTo));
    }

    /// <summary>Second factors are a person's in a shell session: a token's client has its credentials, and no factor.</summary>
    private static async Task<Result<Principal>> PersonAsync(RequestPrincipal principal, IdentityDbContext context, CancellationToken cancellationToken)
    {
        var current = principal.Current!;
        var person = current.Kind == AuthenticationKind.Session
            ? await context.Principals.SingleOrDefaultAsync(p => p.Id == current.Id && p.Type == PrincipalType.User, cancellationToken)
            : null;
        return person is null
            ? Error.Forbidden(IdentityErrors.FactorNotAllowed, "Second factors are for people signed in to the shell. A service account signs in with its client credentials.")
            : person;
    }

    /// <summary>The person and the factor they are adding in this session, while its fifteen minutes last.</summary>
    private static async Task<Result<(Principal Person, AuthenticationChallenge Challenge)>> AddingAsync(RequestPrincipal principal, IdentityDbContext context, AuthenticationChallenges challenges, CancellationToken cancellationToken)
    {
        var person = await PersonAsync(principal, context, cancellationToken);
        if (person.IsFailure)
        {
            return person.Error;
        }

        var challenge = await challenges.FindForSessionAsync(principal.Current!.SessionId, ChallengePurposes.AddFactor, cancellationToken);
        return challenge is null
            ? Error.Conflict(IdentityErrors.FactorInvalid, "Adding this factor took too long, or ended. Start again.")
            : (person.Value, challenge);
    }

    private static WebAuthnOptionsResponse Options(string json) => new(Parse(json));

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
