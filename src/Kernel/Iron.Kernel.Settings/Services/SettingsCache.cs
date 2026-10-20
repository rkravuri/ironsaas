using System.Text.Json;
using Iron.Kernel.Settings.Contracts;

namespace Iron.Kernel.Settings.Services;

/// <summary>A value as the cache holds it: the setting, the place, the value, and who set it when.</summary>
public sealed record StoredValue(string Key, SettingLevel Level, Guid TargetId, JsonElement Value, Guid ChangedBy, DateTimeOffset ChangedAt);

/// <summary>Every value of the tenant as one read found them, by key, and when that read was.</summary>
public sealed class SettingsSnapshot
{
    private readonly Dictionary<string, IReadOnlyList<StoredValue>> _byKey;

    public SettingsSnapshot(IEnumerable<StoredValue> values, DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(values);
        _byKey = values.GroupBy(v => v.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<StoredValue>)[.. g], StringComparer.Ordinal);
        ReadAt = readAt;
        Count = _byKey.Values.Sum(v => v.Count);
    }

    public static SettingsSnapshot Empty { get; } = new([], DateTimeOffset.MinValue);

    public DateTimeOffset ReadAt { get; }

    public int Count { get; }

    public IReadOnlyList<StoredValue> ValuesOf(string key) => _byKey.GetValueOrDefault(key) ?? [];
}

/// <summary>Since when reads of the settings have failed, and the last error.</summary>
public sealed record RefreshFailure(DateTimeOffset Since, string Message);

/// <summary>
/// The values each process answers from (ADR-0036): replaced by every read, kept when a read fails, so the last values
/// stand through an outage of the database (TF-059 AC4). Until the first read the defaults apply.
/// </summary>
public sealed class SettingsCache
{
    private SettingsSnapshot? _current;
    private RefreshFailure? _failing;

    public SettingsSnapshot? Current => Volatile.Read(ref _current);

    /// <summary>Since when reads have failed, and the last error; null while they succeed.</summary>
    public RefreshFailure? Failing => Volatile.Read(ref _failing);

    public void Replace(SettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
        Volatile.Write(ref _failing, null);
    }

    public void Fail(string message, DateTimeOffset now) => Volatile.Write(ref _failing, new RefreshFailure(Failing?.Since ?? now, message));
}

/// <summary>
/// The resolution order (TF-057, TF-221, ADR-0036), pure so it is tested on values alone: the person's own value, then the
/// units from the nearest up to the root, then the tenant, then the default in code. A level the declaration does not
/// allow is skipped, and so is a stored value its rule no longer accepts, so a tightened rule never serves a bad value.
/// </summary>
public static class SettingResolution
{
    public static ResolvedSetting Resolve(SettingDefinition setting, IReadOnlyList<StoredValue> values, Guid? principalId, IReadOnlyList<Guid> unitsNearestFirst)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(unitsNearestFirst);
        var usable = values.Where(v => string.Equals(v.Key, setting.Key, StringComparison.Ordinal) && setting.Problem(v.Value) is null).ToList();

        if (principalId is { } person && setting.Allows(SettingLevel.User) && usable.Find(v => v.Level == SettingLevel.User && v.TargetId == person) is { } own)
        {
            return From(own, null);
        }

        if (setting.Allows(SettingLevel.Unit))
        {
            foreach (var unit in unitsNearestFirst)
            {
                if (usable.Find(v => v.Level == SettingLevel.Unit && v.TargetId == unit) is { } unitValue)
                {
                    return From(unitValue, unit);
                }
            }
        }

        if (setting.Allows(SettingLevel.Tenant) && usable.Find(v => v.Level == SettingLevel.Tenant) is { } tenantValue)
        {
            return From(tenantValue, null);
        }

        return new ResolvedSetting(setting.Key, setting.Default, SettingLevel.Default, null, null, null);
    }

    private static ResolvedSetting From(StoredValue value, Guid? unit) => new(value.Key, value.Value, value.Level, unit, value.ChangedBy, value.ChangedAt);
}
