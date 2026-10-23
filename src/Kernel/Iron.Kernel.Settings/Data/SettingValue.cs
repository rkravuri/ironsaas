using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Settings.Data;

/// <summary>
/// One value set for a setting at one place (TF-057): the tenant, a unit, or a person. The target is the unit's or the
/// person's id, and <see cref="Guid.Empty"/> for the tenant, so one unique index holds one value per place. The value is
/// JSON as the declaration's type writes it; for a secret reference it is the path, never the secret (ADR-0036).
/// </summary>
public sealed class SettingValue : AuditedEntity, ITenantWide, ITrackedChanges
{
    /// <summary>The record type of a value's history (TF-062).</summary>
    public const string RecordType = "setting_value";

    public const int KeyMaxLength = 100;

    private SettingValue()
    {
        Key = string.Empty;
        ValueJson = "null";
    }

    public SettingValue(string key, SettingLevel level, Guid targetId, string valueJson)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueJson);
        if (level == SettingLevel.Default)
        {
            throw new ArgumentException("The default lives in code, not in the table.", nameof(level));
        }

        Key = key;
        Level = level;
        TargetId = level == SettingLevel.Tenant ? Guid.Empty : targetId;
        ValueJson = valueJson;
    }

    public string Key { get; private set; }

    public SettingLevel Level { get; private set; }

    /// <summary>The unit for a unit's value, the person for a person's, <see cref="Guid.Empty"/> for the tenant's.</summary>
    public Guid TargetId { get; private set; }

    /// <summary>The value as JSON (jsonb).</summary>
    public string ValueJson { get; private set; }

    public void Change(string valueJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueJson);
        ValueJson = valueJson;
    }

    /// <summary>A merge moves a unit's value to the unit that stays (ADR-0038).</summary>
    public void MoveToUnit(Guid unitId)
    {
        if (Level != SettingLevel.Unit)
        {
            throw new InvalidOperationException("Only a unit's value moves to another unit.");
        }

        TargetId = unitId;
    }
}

/// <summary>
/// One change of a setting at one place (TF-058 AC1): the value before and after (null for none, so a reset has no
/// after), and who and when (the created pair). Append-only: the app role may only insert and select.
/// </summary>
public sealed class SettingChange : AppendOnlyEntity, ITenantWide
{
    public const string SetKind = "set";
    public const string ResetKind = "reset";

    private SettingChange()
    {
        Key = string.Empty;
        Kind = string.Empty;
    }

    public SettingChange(string key, SettingLevel level, Guid targetId, string kind, string? before, string? after)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        Key = key;
        Level = level;
        TargetId = targetId;
        Kind = kind;
        Before = before;
        After = after;
    }

    public string Key { get; private set; }

    public SettingLevel Level { get; private set; }

    public Guid TargetId { get; private set; }

    /// <summary><c>set</c> or <c>reset</c>.</summary>
    public string Kind { get; private set; }

    /// <summary>The value before (jsonb), or null when there was none at this place.</summary>
    public string? Before { get; private set; }

    /// <summary>The value after (jsonb), or null after a reset.</summary>
    public string? After { get; private set; }
}
