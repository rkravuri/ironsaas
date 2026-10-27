using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Security;

/// <summary>Secure by default (P5, TF-070): headers on every response and hardened cookies; cross-origin access is the Protection module's.</summary>
public static class SecurityExtensions
{
    public static IServiceCollection AddIronSecurity(this IServiceCollection services, IConfiguration configuration, Action<SecurityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new SecurityOptions();
        configuration.GetSection(SecurityOptions.SectionName).Bind(options);
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.Configure<CookiePolicyOptions>(cookies =>
        {
            cookies.Secure = options.Cookies.SecurePolicy;
            cookies.HttpOnly = HttpOnlyPolicy.Always;
            cookies.MinimumSameSitePolicy = SameSiteMode.Strict;
        });

        return services;
    }

    /// <summary>
    /// Headers and the cookie policy. Call after UseIronApi and before the endpoints. Cross-origin access is the Protection
    /// module's (UseIronProtection), which reads the operator's origins from these options and the tenant's from its settings.
    /// </summary>
    public static IApplicationBuilder UseIronSecurity(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseCookiePolicy();
        return app;
    }
}
