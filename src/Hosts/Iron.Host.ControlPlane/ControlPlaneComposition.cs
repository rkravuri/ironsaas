using Iron.Kernel;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.OpenApi;
using Iron.Kernel.Api.Tenancy;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Health;
using Iron.Kernel.Entitlements;
using Iron.Kernel.Events;
using Iron.Kernel.Flags;
using Iron.Kernel.Fleet;
using Iron.Kernel.Identity;
using Iron.Kernel.Monitoring;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Protection;
using Iron.Kernel.Security;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Settings;
using Iron.Kernel.Telemetry;
using Iron.Kernel.Telemetry.Clock;
using Iron.Kernel.Telemetry.Health;
using Iron.Kernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Iron.Host.ControlPlane;

/// <summary>
/// What the control plane is made of, in one place for its image and for the tests that run its database as the image
/// does (ADR-0053): its modules and the services around them, read from the configuration the builder already has.
/// </summary>
public static class ControlPlaneComposition
{
    /// <summary>
    /// The modules this host runs, in order: Audit first (the others write events through its contract), then Events
    /// (Identity and Authorization publish and subscribe here as in a unit, ADR-0060), then Authorization, then Identity
    /// (it replaces the default principal), then Fleet and Tenancy (Tenancy asks Fleet for jobs). The control plane stores
    /// no tenant files. Monitoring watches the platform's own trail and keeps the units' alerts, which Tenancy takes in
    /// from them (ADR-0056).
    /// </summary>
    public static IReadOnlyList<IModule> Modules() =>
        [new AuditModule(), new EventsModule(), new AuthorizationModule(), new IdentityModule(), new FleetModule(), new TenancyModule(), new EntitlementsModule(), new FlagsModule(), new SettingsModule(), new ProtectionModule(), new MonitoringModule(MonitoringScope.ControlPlane)];

    public static WebApplicationBuilder AddControlPlane(this WebApplicationBuilder builder, TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tenant);
        builder.AddIronTelemetry("iron-control-plane", telemetry => telemetry.TenantId = tenant.Id.Value);
        builder.Services.AddIronTenant(tenant);
        builder.Services.AddIronApi();
        builder.Services.AddIronSecurity(builder.Configuration);
        builder.Services.AddIronSecretStore(builder.Configuration);
        builder.Services.AddIronHealth();
        builder.Services.AddIronClockCheck(builder.Configuration);
        builder.Services.AddIronOpenApi("Foundation control plane API");

        // The running control plane connects with its own role; migrate and seed run with the owner's (ADR-0053).
        var connectionString = builder.Configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database is required. Set ConnectionStrings__Database for this unit.");
        builder.Services.AddIronDatabase(connectionString, builder.Configuration["Database:AppRole"], builder.Configuration["Database:AuditReaderRole"]);
        builder.Services.AddIronDatabaseHealthCheck();
        builder.Services.AddModules(builder.Configuration, Modules());
        builder.Services.AddIronFlagReportAtStart();

        // The control plane seals its own audit trail into a chain and anchors it in its store, outside its database (ADR-0053).
        builder.Services.AddAuditSealing(builder.Configuration, AuditAnchoring.OwnStore);

        // The rules over the platform's trail every minute, and every alert, its own and the units', to the operator's alerting (ADR-0056).
        builder.Services.AddMonitoring(MonitoringScope.ControlPlane);
        builder.Services.AddIncidentEscalation();
        return builder;
    }
}
