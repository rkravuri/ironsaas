using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iron.Security.Secrets;

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
            services.AddSingleton<ISecretStore>(_ => new OpenBaoSecretStore(options));
            services.AddHealthChecks().AddCheck<SecretStoreHealthCheck>("secret-store", tags: ["ready"]);
        }

        return services;
    }
}
