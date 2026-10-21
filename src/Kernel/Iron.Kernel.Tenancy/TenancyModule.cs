using FluentValidation;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Seeding;
using Iron.Kernel.Tenancy.Api;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy;

/// <summary>
/// The tenant registry and the control plane's tenant API (TF-001, TF-002), and plans and tenants' entitlements with
/// their history (TF-040 to TF-045, ADR-0034). Needs the Fleet module for jobs and a secret store for the entitlements;
/// the control plane lists both.
/// </summary>
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
        services.AddSingleton(UnitCatalogue.Load());
        services.AddScoped<TenantRegistry>();
        services.AddScoped<ITenantRegistry>(provider => provider.GetRequiredService<TenantRegistry>());
        services.AddScoped<PlanService>();
        services.AddScoped<EntitlementService>();
        services.AddScoped<EntitlementPublisher>();
        services.AddScoped<ITenantEntitlements>(provider => provider.GetRequiredService<EntitlementPublisher>());
        services.AddScoped<ISeeder, PlanSeeder>();
        services.AddScoped<FlagPublications>();
        services.AddScoped<FlagRuleService>();
        services.AddScoped<FlagPublisher>();
        services.AddScoped<ITenantFlags>(provider => provider.GetRequiredService<FlagPublisher>());
        services.AddScoped<FlagReportService>();
        services.AddScoped<IValidator<ReplaceFlagRulesRequest>, ReplaceFlagRulesRequestValidator>();
        services.AddScoped<IValidator<KillSwitchRequest>, KillSwitchRequestValidator>();
        services.AddScoped<IValidator<CreateTenantRequest>, CreateTenantRequestValidator>();
        services.AddScoped<IValidator<AdministratorRecoveryRequest>, AdministratorRecoveryRequestValidator>();
        services.AddScoped<IValidator<SavePlanRequest>, SavePlanRequestValidator>();
        services.AddScoped<IValidator<ApplyPlanRequest>, ApplyPlanRequestValidator>();

        var reconcile = new ReconcileOptions();
        configuration.GetSection(ReconcileOptions.SectionName).Bind(reconcile);
        services.AddSingleton(reconcile);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        TenantsEndpoints.Map(endpoints);
        EntitlementEndpoints.Map(endpoints);
        FlagEndpoints.Map(endpoints);
    }
}

public static class TenancyWorkerExtensions
{
    /// <summary>
    /// The fleet worker's reconcile of the tenants' entitlements (ADR-0034): every minute, a tenant whose store copy is
    /// behind is written again. Only the fleet host calls it.
    /// </summary>
    public static IServiceCollection AddIronEntitlementReconciler(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<EntitlementReconciler>();
        return services;
    }

    /// <summary>
    /// The fleet worker's flag loops (ADR-0035): every minute the flag paths whose store copy is behind are written again,
    /// and once a week the flag report goes to the log (TF-052 AC1). Only the fleet host calls it.
    /// </summary>
    public static IServiceCollection AddIronFlagWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<FlagReconciler>();
        services.AddHostedService(provider => new FlagReportLogger(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<FlagReportLogger>>(), TimeSpan.FromDays(7)));
        return services;
    }

    /// <summary>The flag report in the log once, when the control plane starts (TF-052).</summary>
    public static IServiceCollection AddIronFlagReportAtStart(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService(provider => new FlagReportLogger(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<FlagReportLogger>>()));
        return services;
    }
}
