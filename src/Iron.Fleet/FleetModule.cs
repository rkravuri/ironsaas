using Iron.Api.Modules;
using Iron.Core.Permissions;
using Iron.Data;
using Iron.Fleet.Contracts;
using Iron.Fleet.Data;
using Iron.Fleet.Services;
using Iron.Fleet.Services.Steps;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Fleet;

/// <summary>
/// The fleet (TF-002, TF-210): provisioning jobs, the eight steps and the runner. The control plane API lists it for
/// the jobs; the fleet worker lists it and adds <see cref="ProvisioningWorker"/>. The unit runtime is Compose on the
/// laptop (T14b); without one, the image and unit steps fail with a clear message.
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
        services.AddScoped<ProvisioningRunner>();
        services.TryAddSingleton<IUnitRuntime, NoUnitRuntime>();

        services.AddScoped<IProvisioningStep, ReserveStep>();
        services.AddScoped<IProvisioningStep, DatabaseStep>();
        services.AddScoped<IProvisioningStep, StorageStep>();
        services.AddScoped<IProvisioningStep, SecretsStep>();
        services.AddScoped<IProvisioningStep, MigrateStep>();
        services.AddScoped<IProvisioningStep, SeedStep>();
        services.AddScoped<IProvisioningStep, UnitStep>();
        services.AddScoped<IProvisioningStep, VerifyStep>();
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
        return services;
    }
}
