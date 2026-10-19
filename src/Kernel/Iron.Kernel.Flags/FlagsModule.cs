using Iron.Kernel.Api.Context;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data;
using Iron.Kernel.Flags.Api;
using Iron.Kernel.Flags.Contracts;
using Iron.Kernel.Flags.Data;
using Iron.Kernel.Flags.Services;
using Iron.Kernel.Security.Secrets;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenFeature;

namespace Iron.Kernel.Flags;

/// <summary>
/// The unit's side of feature flags (TF-050, TF-051, TF-054, TF-056; ADR-0035): the rules read from the store every 30
/// seconds and kept for a restart, the in-house OpenFeature provider over them, the clients product code asks, the gate
/// the API's filters ask, and the shell's part of <c>/me/context</c>. It declares the two example flags; the kill switches
/// are the kernel's and every host has them. Hosts list it after Audit and Authorization. The control plane's side (the
/// rules, their history, publishing) is in Iron.Kernel.Tenancy.
/// </summary>
public sealed class FlagsModule : IModule
{
    public string Name => "flags";

    public IReadOnlyList<Permission> Permissions => [];

    public IReadOnlyList<Flag> Flags => ExampleFlags.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new FlagsOptions();
        configuration.GetSection(FlagsOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // TF-056 AC2: a provider this build does not have, or a source it cannot read, stops the host now, with the reason.
        if (!FlagsOptions.Providers.Contains(options.Provider, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Flags:Provider is '{options.Provider}', and this build has no such flag provider. The providers are: {string.Join(", ", FlagsOptions.Providers)}.");
        }

        var store = new SecretStoreOptions();
        configuration.GetSection(SecretStoreOptions.SectionName).Bind(store);
        var fromStore = options.Source switch
        {
            null or "" => store.IsConfigured,
            var source when string.Equals(source, FlagsOptions.StoreSource, StringComparison.OrdinalIgnoreCase) => true,
            var source when string.Equals(source, FlagsOptions.ConfigurationSource, StringComparison.OrdinalIgnoreCase) => false,
            var source => throw new InvalidOperationException($"Flags:Source is '{source}'. Use '{FlagsOptions.StoreSource}' or '{FlagsOptions.ConfigurationSource}'."),
        };
        if (fromStore && !store.IsConfigured)
        {
            throw new InvalidOperationException("Flags:Source is 'store', but no secret store is configured: set Secrets:Address, Secrets:Token and Secrets:Path, or use Flags:Source 'configuration'.");
        }

        if (fromStore)
        {
            services.TryAddSingleton<IFlagRuleSource>(provider => new StoreFlagRuleSource(provider.GetRequiredService<ISecretStore>(), provider.GetRequiredService<SecretStoreOptions>()));
        }
        else
        {
            services.TryAddSingleton<IFlagRuleSource, ConfigurationFlagRuleSource>();
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddIronDbContext<FlagsDbContext>();
        services.AddSingleton<FlagRulesCache>();
        services.AddSingleton<FlagsMetrics>();
        services.AddSingleton<IronFlagProvider>();
        services.AddSingleton<FeatureProvider>(provider => provider.GetRequiredService<IronFlagProvider>());
        services.AddSingleton<OpenFeatureBinding>();
        services.AddSingleton<FlagRefresher>();
        services.AddHostedService(provider => provider.GetRequiredService<FlagRefresher>());
        services.AddScoped<FlagSubjects>();
        services.AddScoped<IFlagClients, FlagClients>();
        services.AddScoped<IFlagGate, FlagGate>();
        services.AddContextPart<FlagsContextPart>(FlagsContextPart.PartName);
        services.AddHealthChecks().AddCheck<FlagsHealthCheck>("flags", HealthStatus.Degraded, ["ready"]);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
    }
}
