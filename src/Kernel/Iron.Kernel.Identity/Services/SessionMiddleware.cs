using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Identity.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Turns the session cookie into the request's principal (TF-020). No cookie: the request runs as nobody and a
/// protected endpoint answers 401 <c>auth.not_authenticated</c>. A cookie of a session that ran out: the session is
/// ended, the cookie is cleared, and the 401 says <c>auth.session_expired</c> so the shell returns to login (TF-021).
/// A person the tenant's policy now requires a second factor of, who has none, keeps the session with a
/// required step: the filters answer 403 <c>identity.mfa.enrolment_required</c> for all but the enrolment (ADR-0047).
/// </summary>
public sealed class SessionMiddleware(RequestDelegate next, IdentityOptions options)
{
    /// <summary>On the first answer after a delegation ended while its delegate acted with it: the shell says so (ADR-0037).</summary>
    public const string ActingEndedHeader = "Iron-Acting-Ended";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cookieName = options.Session.CookieName;
        if (context.Request.Cookies.TryGetValue(cookieName, out var token) && !string.IsNullOrWhiteSpace(token))
        {
            var sessions = context.RequestServices.GetRequiredService<SessionService>();
            var resolution = await sessions.ResolveAsync(token, context.RequestAborted).ConfigureAwait(false);
            if (resolution.IsAlive)
            {
                RequestPrincipal.Set(context, resolution.Principal!);
                if (resolution.MustEnrol)
                {
                    // Every endpoint but the enrolment's own answers this until the person enrols; the session goes on.
                    RequiredStep.Set(context, Error.Forbidden(IdentityErrors.MfaEnrolmentRequired, "Your organisation now requires a second factor of you. Set one up to continue; you stay signed in."));
                }

                if (resolution.ActingEndedFor is { } delegator)
                {
                    context.Response.Headers[ActingEndedHeader] = delegator.ToString("D");
                }
            }
            else
            {
                SessionCookie.Clear(context, options);
                if (resolution.IsExpired)
                {
                    AuthenticationFailure.Set(context, PermissionErrors.SessionExpired);
                }
            }
        }

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>The cookie: only the random token, Secure, HttpOnly, SameSite=Strict, for the whole site, and gone when the browser closes.</summary>
public static class SessionCookie
{
    public static void Set(HttpContext context, IdentityOptions options, string token)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        context.Response.Cookies.Append(options.Session.CookieName, token, Options());
    }

    public static void Clear(HttpContext context, IdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        context.Response.Cookies.Delete(options.Session.CookieName, Options());
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
