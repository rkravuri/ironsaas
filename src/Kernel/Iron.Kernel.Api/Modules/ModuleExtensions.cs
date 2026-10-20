using Iron.Kernel.Api.Context;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Permissions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Api.Modules;

/// <summary>The modules a host runs, in the order the host listed them.</summary>
public sealed class ModuleCollection(IReadOnlyList<IModule> modules)
{
    public IReadOnlyList<IModule> Modules { get; } = modules;
}

public static class ModuleExtensions
{
    /// <summary>Registers the services of every module, the permission and licensing catalogues, and remembers the list for MapModules.</summary>
    public static IServiceCollection AddModules(this IServiceCollection services, IConfiguration configuration, IReadOnlyList<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(modules);

        var names = new HashSet<string>(StringComparer.Ordinal);
        var permissions = new List<Permission>();
        foreach (var module in modules)
        {
            if (!names.Add(module.Name))
            {
                throw new InvalidOperationException($"The module '{module.Name}' is listed twice.");
            }

            foreach (var permission in module.Permissions)
            {
                if (!string.Equals(permission.Module, module.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"The module '{module.Name}' declares '{permission.Name}', but a permission starts with the name of the module that owns it.");
                }

                permissions.Add(permission);
            }

            module.AddServices(services, configuration);
        }

        services.AddSingleton(new ModuleCollection(modules));
        services.AddSingleton(new PermissionCatalogue(permissions));
        services.AddSingleton(LicensingCatalogue.From(modules));
        services.AddSingleton(new SettingDeclarations(modules));
        return services;
    }

    /// <summary>
    /// Maps the endpoints of every module registered with AddModules. A licensed module maps into a group that marks
    /// each of its endpoints with its licence, so the entitlement check refuses them while the module is not entitled
    /// and serves them again when it is, without a restart (ADR-0034: filters, not unmapped endpoints).
    /// </summary>
    public static IEndpointRouteBuilder MapModules(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var modules = endpoints.ServiceProvider.GetRequiredService<ModuleCollection>();
        foreach (var module in modules.Modules)
        {
            if (module.Licence is { } licence)
            {
                var group = endpoints.MapGroup(string.Empty).WithMetadata(new LicensedModuleMetadata(licence.Code));
                group.AddEndpointFilter(new EntitlementFilter());
                module.MapEndpoints(group);
            }
            else
            {
                module.MapEndpoints(endpoints);
            }
        }

        // One /me/context for the parts the modules registered (entitlements, flags, settings): the shell reads one call.
        if (endpoints.ServiceProvider.GetServices<ContextPartRegistration>().Any())
        {
            endpoints.MapContext();
        }

        return endpoints;
    }
}
