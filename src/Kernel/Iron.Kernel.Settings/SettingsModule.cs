using Iron.Kernel.Api.Context;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.History;
using Iron.Kernel.Settings.Api;
using Iron.Kernel.Settings.Contracts;
using Iron.Kernel.Settings.Data;
using Iron.Kernel.Settings.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iron.Kernel.Settings;

/// <summary>
/// Settings in the unit (TF-057 to TF-059, TF-221; ADR-0036): the values set for the tenant, its units and its people,
/// one service that resolves them with a cache of at most 30 seconds, the secrets of secret references read from the
/// store, the console's endpoints, and the shell's part of <c>/me/context</c>. It declares the shell's own preferences.
/// Needs Audit's writer and, to walk up the tree and find a person's home unit, Authorization's and Identity's contracts.
/// </summary>
public sealed class SettingsModule : IModule
{
    public string Name => "settings";

    public IReadOnlyList<Permission> Permissions => SettingsPermissions.All;

    public IReadOnlyList<AuditEventType> AuditEventTypes => SettingsAuditEvents.Types;

    public IReadOnlyList<SettingDefinition> Settings => ShellSettings.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new SettingsOptions();
        configuration.GetSection(SettingsOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.TryAddSingleton(TimeProvider.System);
        services.AddIronDbContext<SettingsDbContext>();
        services.AddSingleton<SettingsCache>();
        services.AddSingleton<SecretCache>();
        services.AddSingleton<SettingsMetrics>();
        services.AddSingleton<SettingsRefresher>();
        services.AddHostedService(provider => provider.GetRequiredService<SettingsRefresher>());
        services.AddScoped<SettingsService>();
        services.AddScoped<ISettings>(provider => provider.GetRequiredService<SettingsService>());
        services.AddScoped<ISecretSettings, SecretSettings>();
        services.AddScoped<SettingsWriter>();

        // A governed setting's approved change applies through the writer (TF-068, ADR-0052).
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConfigurationChangeApplier, SettingChangeApplier>());

        // The security settings of an evidence pack, as they stood at its period's end (TF-067, ADR-0052).
        services.TryAddEnumerable(ServiceDescriptor.Scoped<Iron.Kernel.Compliance.Contracts.IEvidenceSource, SettingsEvidence>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUnitMergeParticipant, UnitSettingsMerge>());
        services.AddScoped<SettingsConsole>();
        services.AddContextPart<SettingsContextPart>(SettingsContextPart.PartName);
        services.AddHealthChecks().AddCheck<SettingsHealthCheck>("settings", HealthStatus.Degraded, ["ready"]);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        SettingsEndpoints.Map(endpoints);
    }
}
