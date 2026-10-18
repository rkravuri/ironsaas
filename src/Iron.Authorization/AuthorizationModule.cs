using Iron.Api.Modules;
using Iron.Api.Permissions;
using Iron.Authorization.Api;
using Iron.Authorization.Contracts;
using Iron.Authorization.Data;
using Iron.Authorization.Services;
using Iron.Data;
using Iron.Data.Seeding;
using Iron.Kernel;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Authorization;

/// <summary>
/// Permissions, roles and assignments (TF-030 to TF-033, thin in S1). Needs Iron.Audit's writer for denial events,
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
