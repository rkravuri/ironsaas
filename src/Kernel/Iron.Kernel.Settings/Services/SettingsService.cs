using System.Collections.Concurrent;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Settings.Contracts;

namespace Iron.Kernel.Settings.Services;

/// <summary>
/// The one effective-value service (TF-057, TF-221, ADR-0036): the values from the cache, the person's unit, and the
/// units above it from the tree's materialised path, one read. The person's unit is the one they work for when it is
/// the request's person (ADR-0038), else their home unit from Identity (through <see cref="IPrincipals"/>). The tree is
/// asked only when a value is set at some unit for the key, so a tenant-wide setting costs no query.
/// </summary>
public sealed class SettingsService(SettingsCache cache, IOrganisationUnits? units = null, IPrincipals? principals = null, ICurrentPrincipal? current = null) : ISettings
{
    public async Task<T> GetAsync<T>(Setting<T> setting, SettingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return setting.Read((await ResolveAsync(setting, context, cancellationToken).ConfigureAwait(false)).Value);
    }

    public async Task<ResolvedSetting> ResolveAsync(SettingDefinition setting, SettingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(context);
        var values = cache.Current?.ValuesOf(setting.Key) ?? [];
        var path = setting.Allows(SettingLevel.Unit) && values.Any(v => v.Level == SettingLevel.Unit)
            ? await UnitsNearestFirstAsync(context, cancellationToken).ConfigureAwait(false)
            : [];
        return SettingResolution.Resolve(setting, values, context.PrincipalId, path);
    }

    private async Task<IReadOnlyList<Guid>> UnitsNearestFirstAsync(SettingContext context, CancellationToken cancellationToken)
    {
        var unit = context.UnitId;
        if (!context.UnitKnown && context.PrincipalId is { } person)
        {
            unit = current is not null && current.Id == person && current.WorkingUnitId is { } working
                ? working
                : principals is null ? null : (await principals.FindAsync([person], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(person)?.HomeUnitId;
        }

        if (unit is not { } start || units is null)
        {
            return [];
        }

        var path = await units.PathAsync(start, cancellationToken).ConfigureAwait(false);
        return [.. path.Reverse()];
    }
}

/// <summary>The secrets read from the store for secret references, by path, each with when it was read.</summary>
public sealed class SecretCache
{
    private readonly ConcurrentDictionary<string, (string? Value, DateTimeOffset ReadAt)> _byPath = new(StringComparer.Ordinal);

    public bool TryGet(string path, DateTimeOffset now, TimeSpan lifetime, out string? value)
    {
        if (_byPath.TryGetValue(path, out var kept) && now - kept.ReadAt < lifetime)
        {
            value = kept.Value;
            return true;
        }

        value = null;
        return false;
    }

    public void Keep(string path, string? value, DateTimeOffset now) => _byPath[path] = (value, now);
}

/// <summary>
/// The secret behind a secret reference (TF-059 AC3, ADR-0036): the reference from the settings, the secret from the
/// store under the key <c>value</c>, kept at most five minutes. The secret is never written anywhere.
/// </summary>
public sealed class SecretSettings(ISettings settings, SecretCache cache, SettingsOptions options, SettingsMetrics metrics, TimeProvider time, ISecretStore? store = null) : ISecretSettings
{
    public async Task<string?> ReadSecretAsync(Setting<SecretReference?> setting, SettingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (await settings.GetAsync(setting, context, cancellationToken).ConfigureAwait(false) is not { } reference)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (cache.TryGet(reference.Path, now, options.SecretLifetime, out var kept))
        {
            return kept;
        }

        if (store is null)
        {
            throw new InvalidOperationException($"The setting '{setting.Key}' refers to '{reference.Path}' in the secret store, and this host has no store configured (Secrets:Address).");
        }

        var secret = await store.ReadAsync(reference.Path, cancellationToken).ConfigureAwait(false);
        metrics.SecretRead();
        var value = secret?.GetValueOrDefault(SecretReference.ValueKey);
        cache.Keep(reference.Path, value, now);
        return value;
    }
}
