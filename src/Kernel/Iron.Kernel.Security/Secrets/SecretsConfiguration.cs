using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace Iron.Kernel.Security.Secrets;

/// <summary>
/// Loads the unit's secret into configuration at start (TF-073): the secret's keys are configuration keys
/// (<c>ConnectionStrings:Database</c>, <c>Storage:ContainerSasUrl</c>, <c>Tokens:SigningKeyPem</c>, ...), so the rest of the
/// host reads configuration as before and the environment holds only the store address, the unit token and the path.
/// It sits below the environment variables, so an explicit environment value still wins.
/// A configured store that cannot be read stops the host with a clear message; a missing configuration means no store.
/// </summary>
public sealed class SecretsConfigurationSource(SecretStoreOptions options, Func<SecretStoreOptions, ISecretStore>? factory = null) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new SecretsConfigurationProvider(options, factory ?? (o => new OpenBaoSecretStore(o)));
}

public sealed class SecretsConfigurationProvider(SecretStoreOptions options, Func<SecretStoreOptions, ISecretStore> factory) : ConfigurationProvider
{
    public override void Load()
    {
        if (!options.IsConfigured)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Path))
        {
            throw new InvalidOperationException("Secrets:Path is required when a secret store is configured: the unit's path under the mount, for example tenants/acme.");
        }

        var store = factory(options);
        try
        {
            IReadOnlyDictionary<string, string>? values;
            try
            {
                values = store.ReadAsync(options.UnitSecretPath, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or SecretStoreException)
            {
                throw new InvalidOperationException($"The secret store at {options.Address} could not be read: {exception.Message}", exception);
            }

            if (values is null)
            {
                throw new InvalidOperationException($"The unit secret '{options.Mount}/{options.UnitSecretPath}' is not in the store at {options.Address}. The fleet writes it when it creates the unit.");
            }

            Data = values.ToDictionary(pair => pair.Key.Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal), pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            (store as IDisposable)?.Dispose();
        }
    }
}

public static class SecretsConfigurationExtensions
{
    /// <summary>
    /// Reads <c>Secrets:*</c> from what is configured so far (environment, files) and, when a store is configured,
    /// adds its unit secret as a configuration source above them. Call right after the builder is created.
    /// </summary>
    public static IConfigurationBuilder AddIronSecrets(this IConfigurationBuilder builder, Func<SecretStoreOptions, ISecretStore>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new SecretStoreOptions();
        builder.Build().GetSection(SecretStoreOptions.SectionName).Bind(options);
        if (options.IsConfigured)
        {
            // Below the environment variables: an explicit environment value wins over the unit secret. The fleet uses
            // that to run the image's migrate and seed commands as the owner role while the unit runs as the app role.
            var environment = builder.Sources.ToList().FindIndex(source => source is EnvironmentVariablesConfigurationSource);
            var source = new SecretsConfigurationSource(options, factory);
            if (environment >= 0)
            {
                builder.Sources.Insert(environment, source);
            }
            else
            {
                builder.Add(source);
            }
        }

        return builder;
    }
}
