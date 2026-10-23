using FluentValidation;
using Iron.Kernel.Api.Context;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data;
using Iron.Kernel.Entitlements.Api;
using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Entitlements.Data;
using Iron.Kernel.Entitlements.Services;
using Iron.Kernel.History;
using Iron.Kernel.Security.Secrets;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iron.Kernel.Entitlements;

/// <summary>
/// The unit's side of entitlements (TF-040, TF-041, TF-043, TF-046; ADR-0034): the answer read from the store every
/// 30 seconds and kept for a restart, the one service that evaluates it, the gate the API's filters ask, seats, and the
/// shell's context. Needs Audit's writer, so hosts list it after Audit; it reads the store through the host's
/// <c>AddIronSecretStore</c>, or its own configuration section in a host without a store.
/// </summary>
public sealed class EntitlementsModule : IModule
{
    public string Name => "entitlements";

    public IReadOnlyList<Permission> Permissions => EntitlementsPermissions.All;

    public IReadOnlyList<AuditEventType> AuditEventTypes => EntitlementsAuditEvents.Types;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new EntitlementsOptions();
        configuration.GetSection(EntitlementsOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        var store = new SecretStoreOptions();
        configuration.GetSection(SecretStoreOptions.SectionName).Bind(store);
        var fromStore = options.Source is null or "" ? store.IsConfigured : string.Equals(options.Source, EntitlementsOptions.StoreSource, StringComparison.OrdinalIgnoreCase);
        if (fromStore)
        {
            services.TryAddSingleton<IEntitlementSource>(provider => new StoreEntitlementSource(provider.GetRequiredService<ISecretStore>(), provider.GetRequiredService<SecretStoreOptions>()));
        }
        else
        {
            services.TryAddSingleton<IEntitlementSource, ConfigurationEntitlementSource>();
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddIronDbContext<EntitlementsDbContext>();
        services.AddSingleton<EntitlementCache>();
        services.AddSingleton<EntitlementsMetrics>();
        services.AddSingleton<EntitlementRefresher>();
        services.AddHostedService(provider => provider.GetRequiredService<EntitlementRefresher>());
        services.AddScoped<SeatService>();
        services.AddScoped<EntitlementEvaluator>();
        services.AddScoped<IEntitlements>(provider => provider.GetRequiredService<EntitlementEvaluator>());
        services.AddScoped<IEntitlementGate>(provider => provider.GetRequiredService<EntitlementEvaluator>());
        services.AddScoped<IValidator<AssignSeatRequest>, AssignSeatRequestValidator>();
        services.AddContextPart<EntitlementsContextPart>(EntitlementsContextPart.PartName);
        services.AddHealthChecks().AddCheck<EntitlementsHealthCheck>("entitlements", HealthStatus.Degraded, ["ready"]);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        EntitlementsEndpoints.Map(endpoints);
    }
}
