using System.Collections.Concurrent;
using Iron.Kernel.Security.Secrets;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// The client secrets of the tenant's single sign-on providers (TF-073, ADR-0048): in the store, never in the database,
/// a log line or an audit event. A unit writes and reads them at <c>tenants/&lt;slug&gt;/sso/&lt;provider id&gt;</c>, which its
/// token may manage and nothing beside.
/// </summary>
public interface ISsoClientSecrets
{
    Task WriteAsync(Guid providerId, string secret, CancellationToken cancellationToken);

    Task<string?> ReadAsync(Guid providerId, CancellationToken cancellationToken);

    Task DeleteAsync(Guid providerId, CancellationToken cancellationToken);
}

/// <summary>The unit's store: one path per provider under the unit's own (<c>Secrets:Path</c>).</summary>
public sealed class StoreSsoClientSecrets(ISecretStore store, SecretStoreOptions options) : ISsoClientSecrets
{
    public const string SecretKey = "client_secret";

    public string PathOf(Guid providerId) => $"{options.Path?.Trim('/')}/sso/{providerId:N}";

    public Task WriteAsync(Guid providerId, string secret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        return store.WriteAsync(PathOf(providerId), new Dictionary<string, string>(StringComparer.Ordinal) { [SecretKey] = secret }, cancellationToken);
    }

    public async Task<string?> ReadAsync(Guid providerId, CancellationToken cancellationToken) =>
        (await store.ReadAsync(PathOf(providerId), cancellationToken).ConfigureAwait(false))?.GetValueOrDefault(SecretKey);

    public Task DeleteAsync(Guid providerId, CancellationToken cancellationToken) => store.DeleteAsync(PathOf(providerId), cancellationToken);
}

/// <summary>
/// A host with no store, run from source on the laptop or in a test that does not ask for one: the secrets live in this
/// process only, and a restart forgets them. Never in the database either; a unit always has its store.
/// </summary>
public sealed class ProcessSsoClientSecrets(ILogger<ProcessSsoClientSecrets> logger) : ISsoClientSecrets
{
    private readonly ConcurrentDictionary<Guid, string> _secrets = new();

    public Task WriteAsync(Guid providerId, string secret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        logger.LogWarning("No secret store is configured: the client secret of single sign-on provider {ProviderId} is kept in this process only.", providerId);
        _secrets[providerId] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> ReadAsync(Guid providerId, CancellationToken cancellationToken) => Task.FromResult(_secrets.GetValueOrDefault(providerId));

    public Task DeleteAsync(Guid providerId, CancellationToken cancellationToken)
    {
        _secrets.TryRemove(providerId, out _);
        return Task.CompletedTask;
    }
}
