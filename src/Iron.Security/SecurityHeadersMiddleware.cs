using Microsoft.AspNetCore.Http;

namespace Iron.Security;

/// <summary>Adds the security headers of TF-070 to every response, including error responses, and forbids caching of API responses.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, SecurityOptions options)
{
    public const string HstsValue = "max-age=31536000; includeSubDomains";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = options.Headers.PermissionsPolicy;
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";

            if (!IsExempt(context.Request.Path))
            {
                headers["Content-Security-Policy"] = options.Headers.ContentSecurityPolicy;
            }

            if (options.Headers.EnableHsts && context.Request.IsHttps)
            {
                headers["Strict-Transport-Security"] = HstsValue;
            }

            if (context.Request.Path.StartsWithSegments("/api"))
            {
                headers["Cache-Control"] = "no-store";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }

    private bool IsExempt(PathString path)
    {
        foreach (var prefix in options.Headers.CspExemptPathPrefixes)
        {
            if (path.StartsWithSegments(prefix))
            {
                return true;
            }
        }

        return false;
    }
}
