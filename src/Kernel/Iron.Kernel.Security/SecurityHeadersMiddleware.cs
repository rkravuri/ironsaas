using Microsoft.AspNetCore.Http;

namespace Iron.Kernel.Security;

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
                headers["Reporting-Endpoints"] = $"{SecurityOptions.HeaderSettings.ReportGroup}=\"{SecurityOptions.HeaderSettings.ReportPath}\"";
            }

            // On every response: behind the ingress the request reaches the host as http, and a browser ignores the header on
            // a response that did not come over https (RFC 6797, section 7.2), so sending it always is safe (ADR-0053).
            if (options.Headers.EnableHsts)
            {
                headers["Strict-Transport-Security"] = HstsValue;
            }

            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/auth") || context.Request.Path.StartsWithSegments("/connect"))
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
