using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data;
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
        services.AddScoped<IProvisioningStep, RemoveStep>();
        services.AddScoped<IProvisioningStep, RecoverStep>();
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
