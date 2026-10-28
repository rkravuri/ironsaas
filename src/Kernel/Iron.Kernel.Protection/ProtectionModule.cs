using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.History;
using Iron.Kernel.Protection.Api;
using Iron.Kernel.Protection.Contracts;
using Iron.Kernel.Protection.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Kernel.Protection;

/// <summary>
/// What protects the web surface beyond the headers (TF-070, TF-072, ADR-0053): cross-origin access only from the tenant's
/// origins, rate limits with the tenant's values within the operator's bounds, the login's proof-of-work challenge after
/// repeated failures, and the report endpoint of the content security policy. It keeps nothing in a database: its
/// counters live in each process until S6. Placement: no business meaning, so Kernel.
/// </summary>
public sealed class ProtectionModule : IModule
{
    public string Name => "protection";

    public IReadOnlyList<Permission> Permissions => ProtectionPermissions.All;

    public IReadOnlyList<AuditEventType> AuditEventTypes => ProtectionAuditEvents.Types;

    public IReadOnlyList<SettingDefinition> Settings => ProtectionSettings.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(ProtectionOptions.SectionName).Get<ProtectionOptions>() ?? new ProtectionOptions();
        if (options.Limits.Count > 0)
        {
            RateLimitBounds.Configure(options.Limits.ToDictionary(pair => pair.Key, pair => (pair.Value.Minimum, pair.Value.Maximum), StringComparer.Ordinal));
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton<ProtectionMetrics>();
        services.AddSingleton<RateLimitGate>();
        services.AddSingleton<CspReports>();
        services.AddSingleton<ILoginChallenges, LoginChallenges>();

        // The check is signed with the tenant's signing key; a host or a test without a store gets the process's keys.
        services.AddIronKeys(configuration);
        services.AddCors();
        services.Replace(ServiceDescriptor.Singleton<ICorsPolicyProvider, TenantCorsPolicyProvider>());
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ProtectionEndpoints.Map(endpoints);
    }
}

public static class ProtectionHosting
{
    /// <summary>
    /// Cross-origin rules and the rate limits, after the principal is known (UseIronIdentity) and before the endpoints, so
    /// a limit is per principal where there is one and per address before sign-in (ADR-0053).
    /// </summary>
    public static IApplicationBuilder UseIronProtection(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseCors();
        app.UseMiddleware<RateLimitMiddleware>();
        return app;
    }
}
