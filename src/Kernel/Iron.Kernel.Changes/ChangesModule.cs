using FluentValidation;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Changes.Api;
using Iron.Kernel.Changes.Contracts;
using Iron.Kernel.Changes.Data;
using Iron.Kernel.Changes.Services;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.History;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Kernel.Changes;

/// <summary>
/// Changes of sensitive configuration that wait for a second administrator (TF-068, ADR-0052): the tenant's four-eyes
/// setting, the pending changes with their before and after, the decisions, and the expiry. It answers the kernel's
/// <see cref="IFourEyes"/>, which every module asks before it saves in a sensitive area; the owning modules apply
/// approved changes through their <see cref="IConfigurationChangeApplier"/>. The first form of TF-247's governed changes,
/// which S5 extends to records and routes through the workflow engine. Listed after the modules whose changes it holds.
/// </summary>
public sealed class ChangesModule : IModule
{
    public string Name => "changes";

    public IReadOnlyList<Permission> Permissions => ChangesPermissions.All;

    public IReadOnlyList<AuditEventType> AuditEventTypes => ChangesAuditEvents.Types;

    public IReadOnlyList<SettingDefinition> Settings => ChangesSettings.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddIronDbContext<ChangesDbContext>();
        services.AddScoped<ChangeApplication>();
        services.AddScoped<FourEyes>();
        services.Replace(ServiceDescriptor.Scoped<IFourEyes>(provider => provider.GetRequiredService<FourEyes>()));
        services.AddScoped<PendingChanges>();
        services.AddScoped<IValidator<DecideChangeRequest>, DecideChangeRequestValidator>();
        services.TryAddScoped<ISignatures, NoSignatures>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISignableRecords, PendingChangeRecords>());
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ChangesEndpoints.Map(endpoints);
    }
}

public static class ChangesHosting
{
    /// <summary>The pass that expires pending changes nobody decided (ADR-0052), for the unit's worker host only.</summary>
    public static IServiceCollection AddChangeExpiry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<ChangeExpiryWorker>();
        return services;
    }
}
