using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Api.Modules;

/// <summary>
/// What a host needs from a module: its name, its permissions, its licence, features, flags and settings, its services and its endpoints.
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

    /// <summary>
    /// The module's feature flags (TF-050, TF-052, ADR-0035), each with its owner, purpose, type, kind, removal date and
    /// default. The kill switches are the kernel's and every host has them; a module never declares a <c>kill.</c> flag.
    /// </summary>
    IReadOnlyList<Flag> Flags => [];

    /// <summary>
    /// The module's settings (TF-057, ADR-0036): typed definitions with key, type, default, rule, the scopes a value may
    /// be set at, whether the shell may see it, and a description. The definition is also the accessor code reads with.
    /// </summary>
    IReadOnlyList<SettingDefinition> Settings => [];

    /// <summary>
    /// The duties a person may hand to someone else for a while (TF-037, ADR-0037), each with the permissions it covers.
    /// The kernel's <c>all</c> is every host's; a module never declares it.
    /// </summary>
    IReadOnlyList<Duty> Duties => [];

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
