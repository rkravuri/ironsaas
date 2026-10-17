using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Api.Modules;

/// <summary>The modules a host runs, in the order the host listed them.</summary>
public sealed class ModuleCollection(IReadOnlyList<IModule> modules)
{
    public IReadOnlyList<IModule> Modules { get; } = modules;
}

public static class ModuleExtensions
{
    /// <summary>Registers the services of every module and remembers the list for MapModules.</summary>
    public static IServiceCollection AddModules(this IServiceCollection services, IConfiguration configuration, IReadOnlyList<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(modules);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            if (!names.Add(module.Name))
            {
                throw new InvalidOperationException($"The module '{module.Name}' is listed twice.");
            }

            module.AddServices(services, configuration);
        }

        services.AddSingleton(new ModuleCollection(modules));
        return services;
    }

    /// <summary>Maps the endpoints of every module registered with AddModules.</summary>
    public static IEndpointRouteBuilder MapModules(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var modules = endpoints.ServiceProvider.GetRequiredService<ModuleCollection>();
        foreach (var module in modules.Modules)
        {
            module.MapEndpoints(endpoints);
        }

        return endpoints;
    }
}
