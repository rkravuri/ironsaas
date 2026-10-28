using FluentValidation;
using Iron.Kernel.Api.ControlPlane;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data;
using Iron.Kernel.History;
using Iron.Kernel.Keys.Api;
using Iron.Kernel.Keys.Contracts;
using Iron.Kernel.Keys.Data;
using Iron.Kernel.Keys.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Kernel.Keys;

/// <summary>
/// A unit's side of key rotation (TF-074, TF-075, ADR-0054): the tenant's keys as the control plane answers, every
/// rotation recorded in the tenant's audit trail with who asked and why, the re-encryption job that moves every
/// encrypted value and search hash of every module to a new version and reports its progress, the tenant's rotation
/// policy, the Keys page, and key checks. The keys themselves are the key service's; rotating them is the operator's.
/// </summary>
public sealed class KeysModule : IModule
{
    public string Name => "keys";

    public IReadOnlyList<Permission> Permissions => KeysPermissions.All;

    public IReadOnlyList<AuditEventType> AuditEventTypes => KeysAuditEvents.Types;

    public IReadOnlyList<SettingDefinition> Settings => KeysSettings.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddIronDbContext<KeysDbContext>();
        services.AddSingleton(configuration.GetSection(KeyWorkerOptions.SectionName).Get<KeyWorkerOptions>() ?? new KeyWorkerOptions());

        // The unit's own client in the control plane, which the Audit module may have registered first (ADR-0050).
        services.TryAddSingleton(configuration.GetSection(ControlPlaneOptions.SectionName).Get<ControlPlaneOptions>() ?? new ControlPlaneOptions());
        services.TryAddSingleton<ControlPlaneClient>();
        services.AddScoped<KeyRotationSync>();
        services.AddScoped<KeyChecks>();
        services.AddScoped<IValidator<KeyCheckRequest>, KeyCheckRequestValidator>();
        services.AddScoped<IValidator<KeyCheckSearchRequest>, KeyCheckSearchRequestValidator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        KeysEndpoints.Map(endpoints);
    }
}

public static class KeysHosting
{
    /// <summary>The unit's key worker (ADR-0054): rotations recorded and values moved, in the worker host only.</summary>
    public static IServiceCollection AddKeyRotation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<KeyWorker>();
        return services;
    }
}
