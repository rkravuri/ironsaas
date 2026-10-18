using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Authorization.Api;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Seeding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Authorization;

/// <summary>
/// Permissions, roles and assignments (TF-030 to TF-033, thin in S1). Needs Iron.Kernel.Audit's writer for denial events,
/// so hosts list the Audit module before this one.
/// </summary>
public sealed class AuthorizationModule : IModule
{
    public string Name => "authz";

    public IReadOnlyList<Permission> Permissions => AuthorizationPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var cacheOptions = new PermissionCacheOptions();
        configuration.GetSection(PermissionCacheOptions.SectionName).Bind(cacheOptions);
        services.AddSingleton(cacheOptions);

        services.AddMemoryCache();
        services.AddIronDbContext<AuthorizationDbContext>();
        services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
        services.AddScoped<IRoleAssignments, RoleAssignments>();
        services.AddScoped<ISeeder, AuthorizationSeeder>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RolesEndpoints.Map(endpoints);
    }
}
