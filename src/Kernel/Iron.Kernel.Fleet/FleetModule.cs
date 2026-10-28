using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Fleet.Data;
using Iron.Kernel.Fleet.Services;
using Iron.Kernel.Fleet.Services.Compose;
using Iron.Kernel.Fleet.Services.Steps;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Fleet;

/// <summary>
/// The fleet (TF-002, TF-210): provisioning jobs, the eight steps and the runner. The control plane API lists it for
/// the jobs; the fleet worker lists it and adds <see cref="ProvisioningWorker"/>. The unit runtime is Compose on the
/// laptop (<c>Fleet:Units:Runtime=compose</c>); without one, the image and unit steps fail with a clear message.
/// </summary>
public sealed class FleetModule : IModule
{
    public string Name => "fleet";

    public IReadOnlyList<Permission> Permissions => FleetPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new FleetOptions();
        configuration.GetSection(FleetOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.AddIronDbContext<FleetDbContext>();
        services.AddScoped<IProvisioningJobs, ProvisioningJobs>();
        services.AddScoped<IJobHandovers, JobHandovers>();
        services.AddScoped<ProvisioningRunner>();
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        if (string.Equals(options.Units.Runtime, FleetOptions.FleetUnitsOptions.ComposeRuntime, StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<IUnitRuntime>(provider => new ComposeUnitRuntime(
                provider.GetRequiredService<IProcessRunner>(),
                new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
                options,
                provider.GetRequiredService<ILogger<ComposeUnitRuntime>>()));
        }
        else
        {
            services.TryAddSingleton<IUnitRuntime, NoUnitRuntime>();
        }

        services.AddScoped<IProvisioningStep, ReserveStep>();
        services.AddScoped<IProvisioningStep, DatabaseStep>();
        services.AddScoped<IProvisioningStep, StorageStep>();
        services.AddScoped<IProvisioningStep, SecretsStep>();
        services.AddScoped<IProvisioningStep, MigrateStep>();
        services.AddScoped<IProvisioningStep, SeedStep>();
        services.AddScoped<IProvisioningStep, UnitStep>();
        services.AddScoped<IProvisioningStep, VerifyStep>();
        services.AddScoped<IProvisioningStep, ValidationPackStep>();
        services.AddScoped<IProvisioningStep, RemoveStep>();
        services.AddScoped<IProvisioningStep, RecoverStep>();
        services.AddScoped<IProvisioningStep, ImpersonateStep>();
        services.AddScoped<IProvisioningStep, BreakGlassOpenStep>();
        services.AddScoped<IProvisioningStep, BreakGlassCloseStep>();

        // The tenants' audit chains, verified by the fleet with each tenant's reader role (TF-061, ADR-0050).
        services.AddSingleton<ArchivedChainReader>();
        services.AddScoped<AuditChainVerifier>();

        // The control plane's own chain (ADR-0053): its API anchors to this store, the fleet verifies against it.
        var controlAudit = configuration.GetSection(ControlAuditOptions.SectionName).Get<ControlAuditOptions>() ?? new ControlAuditOptions();
        services.AddSingleton(controlAudit);
        if (controlAudit.IsConfigured)
        {
            services.AddSingleton<IAuditAnchorStore>(new BlobAuditAnchorStore(controlAudit));
            services.AddScoped<ControlAuditVerifier>();
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

public static class FleetWorkerExtensions
{
    /// <summary>The fleet worker's loop. Only the fleet host calls it; the control plane API serves jobs but does not run them.</summary>
    public static IServiceCollection AddIronFleetWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<ProvisioningWorker>();
        // The fleet image publishes validation packs where every unit's update finds them (ADR-0052), prepares the control
        // plane's roles and anchors, and verifies its chain by hand (ADR-0053).
        services.AddScoped<IUnitCommand, ReleasePackCommand>();
        services.AddScoped<IUnitCommand, ControlPlaneDatabase>();
        services.AddScoped<IUnitCommand, VerifyControlAuditCommand>();
        return services;
    }
}

public static class AuditVerificationHosting
{
    /// <summary>
    /// The fleet host's verification loops: the tenants' audit chains (ADR-0050), and the control plane's own once a day
    /// (ADR-0053). The control plane's API only takes requests for them.
    /// </summary>
    public static IServiceCollection AddIronAuditVerification(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<AuditVerificationWorker>();
        services.AddHostedService<ControlAuditVerificationWorker>();
        return services;
    }
}
