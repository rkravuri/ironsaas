using Iron.Api.Modules;
using Iron.Core.Permissions;
using Iron.Data;
using Iron.Fleet.Contracts;
using Iron.Fleet.Data;
using Iron.Fleet.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Fleet;

/// <summary>The fleet (TF-002, TF-210): provisioning jobs in S1's T13; the worker, the steps and the unit runtime arrive in T14.</summary>
public sealed class FleetModule : IModule
{
    public string Name => "fleet";

    public IReadOnlyList<Permission> Permissions => FleetPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIronDbContext<FleetDbContext>();
        services.AddScoped<IProvisioningJobs, ProvisioningJobs>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
