using Iron.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Api.Tenancy;

/// <summary>The <c>Tenant</c> configuration section: the slug and the hostnames this unit serves.</summary>
public sealed class TenantOptions
{
    public const string SectionName = "Tenant";

    public string? Id { get; set; }

    public string[] Hostnames { get; set; } = [];

    /// <summary>The same hostnames as one comma-separated value, for a rendered file that cannot carry an array (the fleet's Compose template).</summary>
    public string? HostnameList { get; set; }

    public IReadOnlyList<string> AllHostnames()
    {
        var list = (HostnameList ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. Hostnames, .. list];
    }
}

/// <summary>
/// Binds a unit to exactly one tenant at start (TF-003, ADR-0002). The tenant comes from configuration, which the
/// fleet writes when it creates the unit; the control plane binds to <see cref="TenantId.ControlPlane"/>.
/// A missing or invalid binding stops the process with a clear message; a unit never runs unbound.
/// </summary>
public static class TenantBinding
{
    public static TenantContext FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new TenantOptions();
        configuration.GetSection(TenantOptions.SectionName).Bind(options);

        if (!TenantId.TryParse(options.Id, out var id))
        {
            throw new InvalidOperationException(
                $"Tenant:Id is missing or not a valid tenant identifier ('{options.Id}'). Every unit is bound to one tenant; the fleet sets Tenant__Id when it creates the unit.");
        }

        var hostnames = options.AllHostnames();
        if (hostnames.Count == 0)
        {
            throw new InvalidOperationException("Tenant:Hostnames is empty. List the hostnames this unit answers to (Tenant__Hostnames__0, Tenant__Hostnames__1, ... or Tenant__HostnameList=a,b).");
        }

        return new TenantContext(id, hostnames);
    }

    public static IServiceCollection AddIronTenant(this IServiceCollection services, TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tenant);
        services.AddSingleton(tenant);
        return services;
    }

    /// <summary>Refuses requests for hostnames this unit does not serve. Health probes are exempt. Call after UseIronSecurity.</summary>
    public static IApplicationBuilder UseIronTenant(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantHostMiddleware>();
    }
}
