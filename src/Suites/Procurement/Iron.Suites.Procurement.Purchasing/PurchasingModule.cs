using Iron.Kernel;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Seeding;
using Iron.Kernel.History;
using Iron.Suites.Procurement.Purchasing.Api;
using Iron.Suites.Procurement.Purchasing.Contracts;
using Iron.Suites.Procurement.Purchasing.Data;
using Iron.Suites.Procurement.Purchasing.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Suites.Procurement.Purchasing;

/// <summary>
/// The eProcurement Suite's shared master (ADR-0043, ADR-0058), module 1 of the suite's list. Today it holds no business
/// data: its schema records the suite's installation in this tenant, and its About page shows what the tenant has of the
/// suite and under which plan (TF-244, ADR-0059). PR0 fills it with purchasing groups and the purchasing profiles
/// (<c>docs/prd/suites/procurement/purchasing-PRD.md</c>). Licensed with the suite: every plan of the suite includes it.
/// </summary>
public sealed class PurchasingModule : IModule
{
    public string Name => "purchasing";

    public IReadOnlyList<Permission> Permissions => PurchasingPermissions.All;

    public ModuleLicence? Licence { get; } = new(PurchasingLicence.Code, SubscriptionType.Platform);

    public IReadOnlyList<AuditEventType> AuditEventTypes => PurchasingAuditEvents.Types;

    public IReadOnlyList<EventType> Events => PurchasingEvents.All;

    /// <summary>
    /// The eProcurement Suite as hosts compose it, with its modules in the suite's order, the shared master first
    /// (ADR-0042), and its starter plan, Essentials, which holds the Purchasing module; later modules join the suite's
    /// plans as they arrive. The definition lives here while the suite has one module; with the products it moves to
    /// where the hosts compose them, since a master never references a product.
    /// </summary>
    public static SuiteDefinition Suite() => new(
        ProcurementSuite.ShortName,
        ProcurementSuite.DisplayName,
        ProcurementSuite.Description,
        () => [new PurchasingModule()],
        [new StarterPlanEntry(ProcurementSuite.EssentialsPlan, "eProcurement Essentials", 30, [PurchasingLicence.Code], [])]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddIronDbContext<PurchasingDbContext>();
        services.AddScoped<ISeeder, SuiteInstallationSeeder>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        AboutEndpoints.Map(endpoints);
    }
}
