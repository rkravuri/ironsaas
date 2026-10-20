using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Security.Secrets;

namespace Iron.Kernel.Entitlements.Services;

/// <summary>
/// Where a unit's entitlements come from. A read that fails throws, so the refresh keeps the last known answer; a read
/// that finds nothing answers null, which means nothing is licensed (the control plane has published nothing yet).
/// </summary>
public interface IEntitlementSource
{
    /// <summary>Where the answer comes from, for logs and the readiness detail.</summary>
    string Describe();

    Task<EntitlementsDocument?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The store (ADR-0034): <c>tenants/&lt;slug&gt;/entitlements</c>, which the control plane writes and the unit's token may
/// read. The unit never calls the control plane.
/// </summary>
public sealed class StoreEntitlementSource(ISecretStore store, SecretStoreOptions options) : IEntitlementSource
{
    public string Path => $"{options.Path?.Trim('/')}/{EntitlementsDocument.SecretName}";

    public string Describe() => $"the store at {options.Address}, {options.Mount}/{Path}";

    public async Task<EntitlementsDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        var secret = await store.ReadAsync(Path, cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            return null;
        }

        return secret.TryGetValue(EntitlementsDocument.DocumentKey, out var json)
            ? EntitlementsDocument.Parse(json)
            : throw new FormatException($"The secret {options.Mount}/{Path} has no '{EntitlementsDocument.DocumentKey}' key.");
    }
}

/// <summary>The <c>Entitlements</c> configuration section, for a host that has no store or is no tenant.</summary>
public sealed class ConfigurationEntitlementSource(EntitlementsOptions options, TenantContext? tenant = null) : IEntitlementSource
{
    public string Describe() => "the Entitlements configuration section";

    public Task<EntitlementsDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        var modules = options.Modules
            .Select(m => new ModuleEntitlement(m.Code, m.Status, m.SubscriptionType, m.UserLimit, m.StartsOn ?? DateOnly.MinValue, m.EndsOn))
            .ToList();
        var features = options.Features.Distinct(StringComparer.Ordinal).Select(code => new FeatureEntitlement(code, EntitlementStatus.Enabled, DateOnly.MinValue, null)).ToList();
        var document = new EntitlementsDocument(EntitlementsDocument.CurrentFormat, tenant?.Id.Value ?? "local", 0, EntitlementsOptions.ConfigurationSource, options.GraceDays, DateTimeOffset.UnixEpoch, modules, features);
        return Task.FromResult<EntitlementsDocument?>(document);
    }
}
