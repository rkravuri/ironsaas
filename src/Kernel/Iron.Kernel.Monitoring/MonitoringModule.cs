using FluentValidation;
using Iron.Kernel.Api.ControlPlane;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data;
using Iron.Kernel.History;
using Iron.Kernel.Monitoring.Api;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Monitoring.Data;
using Iron.Kernel.Monitoring.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Kernel.Monitoring;

/// <summary>
/// Security monitoring (TF-079, ADR-0056): alert rules with thresholds over the audit trail, run every minute, the alerts
/// they raise with acknowledge and resolve, a failing rule as an alert of its own, and the way to the operator. In a unit
/// it watches the tenant's trail and copies each alert to the control plane; in the control plane it watches the
/// platform's own trail, keeps the units' copies, and sends every alert to the operator's alerting. Placement: no
/// business meaning, so Kernel. The host says which by the scope it lists the module with.
/// </summary>
public sealed class MonitoringModule(MonitoringScope scope) : IModule
{
    /// <summary>A unit's module, as the hosts' lists and the module catalogue create it.</summary>
    public MonitoringModule()
        : this(MonitoringScope.Unit)
    {
    }

    public string Name => "monitoring";

    public IReadOnlyList<Permission> Permissions => MonitoringPermissions.All;

    public IReadOnlyList<AuditEventType> AuditEventTypes => MonitoringAuditEvents.Types;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddIronDbContext<MonitoringDbContext>();
        var options = configuration.GetSection(MonitoringOptions.SectionName).Get<MonitoringOptions>() ?? new MonitoringOptions();
        services.AddSingleton(options);
        services.AddSingleton(options.Alertmanager);
        services.AddSingleton(new MonitoringPlace(scope));
        services.AddScoped<RuleEvaluator>();
        services.AddScoped<AlertService>();
        services.AddScoped<IValidator<AlertNoteRequest>, AlertNoteRequestValidator>();
        services.AddScoped<IValidator<AlertRuleValuesRequest>, AlertRuleValuesRequestValidator>();
        if (scope == MonitoringScope.Unit)
        {
            // The unit's own client in the control plane, which the Audit or Keys module may have registered first (ADR-0050).
            services.TryAddSingleton(configuration.GetSection(ControlPlaneOptions.SectionName).Get<ControlPlaneOptions>() ?? new ControlPlaneOptions());
            services.TryAddSingleton<ControlPlaneClient>();
            services.AddScoped<AlertCopySender>();
        }
        else
        {
            services.AddScoped<IAlertCopies, AlertCopyStore>();
            services.AddHttpClient<AlertmanagerForwarder>(client => client.Timeout = TimeSpan.FromSeconds(10));
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        MonitoringEndpoints.Map(endpoints);
    }
}

public static class MonitoringHosting
{
    /// <summary>
    /// The rules every minute, and the way to the operator: in a unit's worker, the copies to the control plane; in the
    /// control plane, the operator's alerting. One host per database runs it; the pass takes a lock of its own anyway.
    /// </summary>
    public static IServiceCollection AddMonitoring(this IServiceCollection services, MonitoringScope scope)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<RuleEvaluationWorker>();
        if (scope == MonitoringScope.Unit)
        {
            services.AddHostedService<AlertCopyWorker>();
        }
        else
        {
            services.AddHostedService<AlertmanagerWorker>();
        }

        return services;
    }
}
