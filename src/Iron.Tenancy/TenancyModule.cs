using FluentValidation;
using Iron.Api.Modules;
using Iron.Data;
using Iron.Kernel;
using Iron.Tenancy.Api;
using Iron.Tenancy.Contracts;
using Iron.Tenancy.Data;
using Iron.Tenancy.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Tenancy;

/// <summary>The tenant registry and the control plane's tenant API (TF-001, TF-002). Needs the Fleet module for jobs; the control plane lists both.</summary>
public sealed class TenancyModule : IModule
{
    public string Name => "tenancy";

    public IReadOnlyList<Permission> Permissions => TenancyPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new TenancyOptions();
        configuration.GetSection(TenancyOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.AddIronDbContext<TenancyDbContext>();
        services.AddScoped<TenantRegistry>();
        services.AddScoped<ITenantRegistry>(provider => provider.GetRequiredService<TenantRegistry>());
        services.AddScoped<IValidator<CreateTenantRequest>, CreateTenantRequestValidator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        TenantsEndpoints.Map(endpoints);
    }
}
