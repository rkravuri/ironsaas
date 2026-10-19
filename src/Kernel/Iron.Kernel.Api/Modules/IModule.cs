using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Api.Modules;

/// <summary>
/// What a host needs from a module: its name, its permissions, its licence and features, its services and its endpoints.
/// A host lists its modules in Program.cs and calls AddModules and MapModules (ADR-0001: explicit composition, no assembly scanning).
/// </summary>
public interface IModule
{
    /// <summary>The module name in snake_case, the same as its database schema and the first segment of its permissions: <c>identity</c>, <c>authz</c>.</summary>
    string Name { get; }

    /// <summary>Every permission this module declares (TF-030). Each name starts with the module name and a dot.</summary>
    IReadOnlyList<Permission> Permissions { get; }

    /// <summary>
    /// How the module is licensed (ADR-0034). A kernel module is the platform and has none, which is the default; a
    /// Business or Products module declares its licence code and subscription type, and MapModules puts every endpoint
    /// it maps behind the entitlement check.
    /// </summary>
    ModuleLicence? Licence => null;

    /// <summary>The parts of the module licensed on their own (TF-046), in any layer. Endpoints name them with <c>RequireFeature</c>.</summary>
    IReadOnlyList<Feature> Features => [];

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
