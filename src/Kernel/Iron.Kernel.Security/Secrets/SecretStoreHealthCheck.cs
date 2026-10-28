using Iron.Kernel.Security.Keys;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Security.Secrets;

/// <summary>Readiness check (TF-133): the secret store answers and is unsealed.</summary>
public sealed class SecretStoreHealthCheck(ISecretStore store, SecretStoreOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return await store.IsReachableAsync(cancellationToken).ConfigureAwait(false)
            ? HealthCheckResult.Healthy("The secret store answers.")
            : HealthCheckResult.Unhealthy($"The secret store at {options.Address} does not answer or is sealed.");
    }
}

public static class SecretStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the store and its readiness check when <c>Secrets:Address</c> and <c>Secrets:Token</c> are configured;
    /// otherwise nothing, so the laptop runs without a store. The key service comes with it (ADR-0054): the store's transit
    /// engine, or the process's own keys without a store.
    /// </summary>
    public static IServiceCollection AddIronSecretStore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new SecretStoreOptions();
        configuration.GetSection(SecretStoreOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // The handover place (ADR-0046): only an image command the fleet runs is given one.
        var handover = new HandoverOptions();
        configuration.GetSection(HandoverOptions.SectionName).Bind(handover);
        services.AddSingleton(handover);
        if (handover.IsConfigured && options.IsConfigured)
        {
            services.AddSingleton<Iron.Kernel.Handover.IOperatorHandover>(provider => new StoreOperatorHandover(options, handover, provider.GetService<TimeProvider>() ?? TimeProvider.System));
        }
        else
        {
            services.AddSingleton<Iron.Kernel.Handover.IOperatorHandover, Iron.Kernel.Handover.NoOperatorHandover>();
        }
        if (options.IsConfigured)
        {
            services.AddSingleton(_ => new OpenBaoSecretStore(options));
            services.AddSingleton<ISecretStore>(provider => provider.GetRequiredService<OpenBaoSecretStore>());
            services.AddSingleton<ISecretStoreAdmin>(provider => provider.GetRequiredService<OpenBaoSecretStore>());
            services.AddHealthChecks().AddCheck<SecretStoreHealthCheck>("secret-store", tags: ["ready"]);
        }
        else
        {
            // Code that needs the store (the fleet's steps) gets a clear message instead of a missing registration.
            services.AddSingleton<ISecretStore, UnconfiguredSecretStore>();
            services.AddSingleton<ISecretStoreAdmin, UnconfiguredSecretStore>();
        }

        return services.AddIronKeys(configuration, options);
    }

    /// <summary>
    /// The key service (ADR-0054): OpenBao's transit engine with this unit's key family (<c>Keys:Prefix</c>) when a store
    /// is configured, else the keys of this process. A test or a module that registered one first keeps it.
    /// </summary>
    public static IServiceCollection AddIronKeys(this IServiceCollection services, IConfiguration configuration, SecretStoreOptions? store = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (store is null)
        {
            store = new SecretStoreOptions();
            configuration.GetSection(SecretStoreOptions.SectionName).Bind(store);
        }

        var keys = new KeyOptions();
        configuration.GetSection(KeyOptions.SectionName).Bind(keys);
        services.TryAddSingleton(keys);
        if (store.IsConfigured)
        {
            services.TryAddSingleton(_ => new TransitKeyService(store, keys));
            services.TryAddSingleton<IKeyService>(provider => provider.GetRequiredService<TransitKeyService>());
            services.TryAddSingleton<IKeyAdministration>(provider => provider.GetRequiredService<TransitKeyService>());
        }
        else
        {
            services.TryAddSingleton(provider => new LocalKeyService(keys, provider.GetService<ILogger<LocalKeyService>>()));
            services.TryAddSingleton<IKeyService>(provider => provider.GetRequiredService<LocalKeyService>());
            services.TryAddSingleton<IKeyAdministration>(provider => provider.GetRequiredService<LocalKeyService>());
        }

        return services;
    }
}

/// <summary>Stands in when no store is configured; every call explains what is missing.</summary>
public sealed class UnconfiguredSecretStore : ISecretStore, ISecretStoreAdmin
{
    private const string Message = "No secret store is configured: set Secrets:Address, Secrets:Token and Secrets:Path.";

    public Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken) => throw new InvalidOperationException(Message);

    public Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) => throw new InvalidOperationException(Message);

    public Task<bool> IsReachableAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public Task CreateReadPolicyAsync(string policyName, IReadOnlyList<string> paths, CancellationToken cancellationToken, IReadOnlyList<string>? managedPaths = null, IReadOnlyList<KeyGrant>? keys = null) => throw new InvalidOperationException(Message);

    public Task<string> CreateTokenAsync(IReadOnlyList<string> policies, string displayName, TimeSpan timeToLive, CancellationToken cancellationToken) => throw new InvalidOperationException(Message);

    public Task WriteOnceAsync(string path, IReadOnlyDictionary<string, string> values, TimeSpan lifetime, CancellationToken cancellationToken) => throw new InvalidOperationException(Message);

    public Task DeleteAsync(string path, CancellationToken cancellationToken) => throw new InvalidOperationException(Message);

    public Task CreateDepositPolicyAsync(string policyName, string pathPattern, CancellationToken cancellationToken) => throw new InvalidOperationException(Message);
}
