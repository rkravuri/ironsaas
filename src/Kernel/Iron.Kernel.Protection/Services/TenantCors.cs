using Iron.Kernel.Protection.Contracts;
using Iron.Kernel.Security;
using Iron.Kernel.Settings.Contracts;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Protection.Services;

/// <summary>
/// Cross-origin access only from the origins the tenant lists (TF-070 AC2, ADR-0053), and those the operator configures for
/// every tenant (<c>Security:Cors:AllowedOrigins</c>). Read at each request from the settings' cache, so a change applies
/// within 30 seconds. A request from any other origin gets no CORS answer, and the browser refuses the page its response;
/// the API's own pages are the same origin and need none.
/// </summary>
public sealed class TenantCorsPolicyProvider(SecurityOptions security) : ICorsPolicyProvider
{
    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        ArgumentNullException.ThrowIfNull(context);
        var origins = new HashSet<string>(security.Cors.AllowedOrigins.Select(o => o.TrimEnd('/').ToLowerInvariant()), StringComparer.Ordinal);
        if (context.RequestServices.GetService<ISettings>() is { } settings)
        {
            var listed = await settings.GetAsync(ProtectionSettings.AllowedOrigins, SettingContext.Tenant, context.RequestAborted).ConfigureAwait(false);
            origins.UnionWith(ProtectionSettings.OriginsOf(listed));
        }

        var policy = new CorsPolicyBuilder();
        if (origins.Count == 0)
        {
            return policy.Build();
        }

        return policy
            .WithOrigins([.. origins])
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .WithExposedHeaders(CorrelationId.HeaderName, "Retry-After")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))
            .Build();
    }
}
