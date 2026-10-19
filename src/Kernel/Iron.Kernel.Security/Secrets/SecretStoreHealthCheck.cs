using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
    /// <summary>Registers the store and its readiness check when <c>Secrets:Address</c> and <c>Secrets:Token</c> are configured; otherwise nothing, so the laptop runs without a store.</summary>
    public static IServiceCollection AddIronSecretStore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new SecretStoreOptions();
        configuration.GetSection(SecretStoreOptions.SectionName).Bind(options);
        services.AddSingleton(options);
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

    public Task CreateReadPolicyAsync(string policyName, IReadOnlyList<string> paths, CancellationToken cancellationToken) => throw new InvalidOperationException(Message);

    public Task<string> CreateTokenAsync(IReadOnlyList<string> policies, string displayName, TimeSpan timeToLive, CancellationToken cancellationToken) => throw new InvalidOperationException(Message);
}
