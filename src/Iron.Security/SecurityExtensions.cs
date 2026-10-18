using Iron.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Security;

/// <summary>Secure by default (P5, TF-070): headers on every response, hardened cookies, cross-origin access only for configured origins.</summary>
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

        if (options.Cors.AllowedOrigins.Length > 0)
        {
            services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
                .WithOrigins(options.Cors.AllowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                .WithExposedHeaders(CorrelationId.HeaderName)));
        }

        return services;
    }

    /// <summary>Headers, cookie policy and (when origins are configured) CORS. Call after UseIronApi and before the endpoints.</summary>
    public static IApplicationBuilder UseIronSecurity(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.ApplicationServices.GetRequiredService<SecurityOptions>();

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseCookiePolicy();
        if (options.Cors.AllowedOrigins.Length > 0)
        {
            app.UseCors();
        }

        return app;
    }
}
