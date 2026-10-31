using Iron.Kernel.History;
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
    /// The suite the module belongs to (ADR-0043): its short name, such as <c>procurement</c>, or <c>foundation</c> for a
    /// kernel or business module. It comes from the project's <c>IronSuite</c>, which the build writes into the assembly,
    /// so a module never states it in code; the catalogue groups by it.
    /// </summary>
    string Suite => SuiteName.Of(GetType().Assembly);

    /// <summary>
    /// How the module is licensed (ADR-0034). A kernel module is the platform and has none, which is the default; a
    /// Business or suite module declares its licence code and subscription type, and MapModules puts every endpoint
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

    /// <summary>
    /// The audit event types the module writes, each with the words it reads as (ADR-0051): the audit trail, the History
    /// tab and the exports show the name. A test fails for an event constant no module names.
    /// </summary>
    IReadOnlyList<AuditEventType> AuditEventTypes => [];

    /// <summary>
    /// The domain events the module publishes (TF-110, ADR-0060), each declared in its Contracts with its name, version,
    /// record type, data, description and owner. Each name starts with the module's name; the catalogue lists them.
    /// </summary>
    IReadOnlyList<EventType> Events => [];

    /// <summary>
    /// The events the module subscribes to (TF-111, ADR-0060), each by the publishing module's declaration, with the
    /// handler AddModules registers. The worker's dispatcher hands every such event to it, at least once, in order per record.
    /// </summary>
    IReadOnlyList<EventSubscription> Subscriptions => [];

    /// <summary>
    /// The module's jobs (TF-120, ADR-0061), each with its schedule or trigger, scope, timeout, retry policy, concurrency,
    /// priority class and owner. AddModules registers each handler; the worker runs a unit's jobs once per interval, and
    /// every process its own jobs of <see cref="JobScope.EachProcess"/>.
    /// </summary>
    IReadOnlyList<JobDefinition> Jobs => [];

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
