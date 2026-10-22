using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Turns the session cookie into the request's principal (TF-020). No cookie: the request runs as nobody and a
/// protected endpoint answers 401 <c>auth.not_authenticated</c>. A cookie of a session that ran out: the session is
/// ended, the cookie is cleared, and the 401 says why, so the shell returns to login and says so (TF-021, TF-022).
/// A person the tenant's policy now requires a second factor of, who has none, keeps the session with a
/// required step: the filters answer 403 <c>identity.mfa.enrolment_required</c> for all but the enrolment (ADR-0047).
/// While someone is signed in as another person, their impersonation's cookie comes first; when it has ended, its cookie
/// is cleared, the answer says so, and the request runs as the impersonator's own session again (TF-025 AC3, ADR-0049).
/// </summary>
public sealed class SessionMiddleware(RequestDelegate next, IdentityOptions options)
{
    /// <summary>On the first answer after a delegation ended while its delegate acted with it: the shell says so (ADR-0037).</summary>
    public const string ActingEndedHeader = "Iron-Acting-Ended";

    /// <summary>
    /// On every answer of a live session: when it ends without further activity, as the server counts it, so the shell's
    /// warning is exact when a tenant shortens its limits (ADR-0033, folded into S3 T5).
    /// </summary>
    public const string IdleEndsHeader = "Iron-Session-Idle-Ends";

    /// <summary>On the first answer after an impersonation ended: the shell says so and reads /me again (TF-025 AC3).</summary>
    public const string ImpersonationEndedHeader = "Iron-Impersonation-Ended";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sessions = new Lazy<SessionService>(() => context.RequestServices.GetRequiredService<SessionService>());
        var impersonationEnded = false;
        if (Token(context, options.Session.ImpersonationCookieName) is { } impersonation)
        {
            var resolution = await sessions.Value.ResolveAsync(impersonation, context.RequestAborted).ConfigureAwait(false);
            if (resolution.IsAlive)
            {
                Accept(context, resolution);
                await next(context).ConfigureAwait(false);
                return;
            }

            SessionCookie.Clear(context, options, options.Session.ImpersonationCookieName);
            if (resolution.IsExpired)
            {
                impersonationEnded = true;
                context.Response.Headers[ImpersonationEndedHeader] = "true";
            }
        }

        if (Token(context, options.Session.CookieName) is { } token)
        {
            var resolution = await sessions.Value.ResolveAsync(token, context.RequestAborted).ConfigureAwait(false);
            if (resolution.IsAlive)
            {
                Accept(context, resolution);
            }
            else
            {
                SessionCookie.Clear(context, options);
                if (resolution.IsExpired)
                {
                    AuthenticationFailure.Set(context, CodeOf(resolution.ExpiryReason));
                }
            }
        }
        else if (impersonationEnded)
        {
            // The operator has no session of their own here to return to (ADR-0049).
            AuthenticationFailure.Set(context, PermissionErrors.ImpersonationEnded);
        }

        await next(context).ConfigureAwait(false);
    }

    private static string? Token(HttpContext context, string cookie) =>
        context.Request.Cookies.TryGetValue(cookie, out var token) && !string.IsNullOrWhiteSpace(token) ? token : null;

    private static void Accept(HttpContext context, SessionResolution resolution)
    {
        RequestPrincipal.Set(context, resolution.Principal!);
        context.Response.Headers[IdleEndsHeader] = resolution.Principal!.IdleExpiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        if (resolution.TermsRequired)
        {
            // Every endpoint but the terms' own answers this until the person accepts; terms come before enrolment (ADR-0049).
            RequiredStep.Set(context, Error.Forbidden(IdentityErrors.TermsAcceptanceRequired, "A document you must accept has a new version. Read and accept it to continue; you stay signed in."));
        }
        else if (resolution.MustEnrol)
        {
            // Every endpoint but the enrolment's own answers this until the person enrols; the session goes on.
            RequiredStep.Set(context, Error.Forbidden(IdentityErrors.MfaEnrolmentRequired, "Your organisation now requires a second factor of you. Set one up to continue; you stay signed in."));
        }

        if (resolution.ActingEndedFor is { } delegator)
        {
            context.Response.Headers[ActingEndedHeader] = delegator.ToString("D");
        }
    }

    /// <summary>Why the session ended, as the 401 says it: the shell's login page tells the person (TF-021 AC2, TF-022).</summary>
    public static string CodeOf(string? reason) => reason switch
    {
        SessionEndReasons.Limit => PermissionErrors.SessionLimit,
        SessionEndReasons.EndedByAdministrator => PermissionErrors.SessionEnded,
        SessionEndReasons.Revoked => PermissionErrors.SessionRevoked,
        SessionEndReasons.ImpersonationEnded => PermissionErrors.ImpersonationEnded,
        _ => PermissionErrors.SessionExpired,
    };
}

/// <summary>The cookie: only the random token, Secure, HttpOnly, SameSite=Strict, for the whole site, and gone when the browser closes.</summary>
public static class SessionCookie
{
    public static void Set(HttpContext context, IdentityOptions options, string token)
    {
        ArgumentNullException.ThrowIfNull(options);
        Set(context, options.Session.CookieName, token);
    }

    public static void Set(HttpContext context, string cookieName, string token)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Cookies.Append(cookieName, token, Options());
    }

    public static void Clear(HttpContext context, IdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Clear(context, options, options.Session.CookieName);
    }

    public static void Clear(HttpContext context, IdentityOptions options, string cookieName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        context.Response.Cookies.Delete(cookieName, Options());
    }

    private static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };
}
