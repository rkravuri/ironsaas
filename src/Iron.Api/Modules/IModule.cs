using Iron.Kernel;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Api.Modules;

/// <summary>
/// What a host needs from a module: its name, its permissions, its services and its endpoints. A host lists its
/// modules in Program.cs and calls AddModules and MapModules (ADR-0001: explicit composition, no assembly scanning).
/// </summary>
public interface IModule
{
    /// <summary>The module name in snake_case, the same as its database schema and the first segment of its permissions: <c>identity</c>, <c>authz</c>.</summary>
    string Name { get; }

    /// <summary>Every permission this module declares (TF-030). Each name starts with the module name and a dot.</summary>
    IReadOnlyList<Permission> Permissions { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
