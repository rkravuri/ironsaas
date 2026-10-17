using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Api.Modules;

/// <summary>
/// What a host needs from a module: its name, its services and its endpoints. A host lists its modules in
/// Program.cs and calls AddModules and MapModules (ADR-0001: explicit composition, no assembly scanning).
/// Permissions join this interface in T8.
/// </summary>
public interface IModule
{
    /// <summary>The module name in snake_case, the same as its database schema: <c>identity</c>.</summary>
    string Name { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
