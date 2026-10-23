using System.Text.Json;
using Iron.Kernel.History;

namespace Iron.Kernel.Settings.Contracts;

/// <summary>
/// Who or what a setting is read for (TF-057, TF-221): a person (their own value, then their home unit walking up, then
/// the tenant), a unit (a record's owning unit: the unit walking up, then the tenant), or the tenant alone.
/// </summary>
public sealed record SettingContext
{
    private SettingContext(Guid? principalId, Guid? unitId, bool unitKnown)
    {
        PrincipalId = principalId;
        UnitId = unitId;
        UnitKnown = unitKnown;
    }

    /// <summary>The person whose own value counts first; none for a unit or the tenant.</summary>
    public Guid? PrincipalId { get; }

    /// <summary>The unit to walk up from: the unit itself, or the person's home unit.</summary>
    public Guid? UnitId { get; }

    /// <summary>False for a person whose home unit the settings service looks up.</summary>
    public bool UnitKnown { get; }

    /// <summary>The tenant's value, else the default: for what has no person and no unit, such as a session's limits.</summary>
    public static SettingContext Tenant { get; } = new(null, null, true);

    /// <summary>A unit, for a record it owns (TF-221): the unit's value, then its parents', then the tenant's.</summary>
    public static SettingContext ForUnit(Guid unitId) => new(null, unitId, true);

    /// <summary>A person: their own value, then their home unit walking up, then the tenant. The home unit is looked up.</summary>
    public static SettingContext ForPerson(Guid principalId) => new(principalId, null, false);

    /// <summary>A person whose home unit the caller knows already (none for a person without one).</summary>
    public static SettingContext ForPerson(Guid principalId, Guid? homeUnitId) => new(principalId, homeUnitId, true);
}

/// <summary>
/// An effective value and where it comes from: the level, the unit for a unit's value, and who set it when. A default
/// has no unit, no author and no time.
/// </summary>
public sealed record ResolvedSetting(string Key, JsonElement Value, SettingLevel Level, Guid? UnitId, Guid? ChangedBy, DateTimeOffset? ChangedAt);

/// <summary>
/// The one service that answers effective settings (TF-057, TF-059, ADR-0036). Values come from a cache of at most
/// 30 seconds, so a change reaches every process within that time without a restart. For a secret reference it answers
/// the reference; the secret itself is <see cref="ISecretSettings"/>'s.
/// </summary>
public interface ISettings
{
    /// <summary>The effective value as the setting's type: <c>await settings.GetAsync(IdentitySettings.SessionIdleMinutes, SettingContext.Tenant, ct)</c>.</summary>
    Task<T> GetAsync<T>(Setting<T> setting, SettingContext context, CancellationToken cancellationToken);

    /// <summary>The effective value with where it comes from.</summary>
    Task<ResolvedSetting> ResolveAsync(SettingDefinition setting, SettingContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The secrets that secret-reference settings point at (TF-059 AC3). The value is read from the store when asked and kept
/// at most five minutes; it is never written to the database, a log, an audit event or an answer of the API.
/// </summary>
public interface ISecretSettings
{
    /// <summary>The secret for this context, or null when no reference is set or the store holds nothing at it.</summary>
    Task<string?> ReadSecretAsync(Setting<SecretReference?> setting, SettingContext context, CancellationToken cancellationToken);
}

public static class SettingsPermissions
{
    public static readonly Permission Read = new("settings.values.read", "See the settings of the tenant and its units, where each value comes from, and who changed it.", Risk.Low);

    public static readonly Permission Manage = new("settings.values.manage", "Change and reset settings: for the whole tenant when held tenant-wide, for a unit when held at the unit.", Risk.High);

    public static IReadOnlyList<Permission> All => [Read, Manage];
}

public static class SettingsAuditEvents
{
    public const string ValueChanged = "settings.value_changed";
    public const string ValueReset = "settings.value_reset";

    /// <summary>Every event type above with the words it reads as in the audit trail, the History tab and the exports (ADR-0051).</summary>
    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        new(ValueChanged, "Setting changed"),
        new(ValueReset, "Setting reset"),
    ];
}

public static class SettingsErrors
{
    public const string Unknown = "settings.setting.unknown";
    public const string ScopeNotAllowed = "settings.scope.not_allowed";
    public const string ValueInvalid = "settings.value.invalid";
    public const string UnitUnknown = "settings.unit.unknown";
    public const string ScopeInvalid = "settings.scope.invalid";
}

/// <summary>
/// The shell's own settings (ADR-0036): a person's preferences, which follow them to every browser they sign in on. The
/// browser keeps a copy that <c>public/scheme.js</c> reads before the first paint.
/// </summary>
public static class ShellSettings
{
    public static readonly Setting<string> ColourScheme = Setting.Choice(
        "shell.colour_scheme", "The colour scheme of the shell: light, dark, or the one the device uses (auto).", "auto", ["light", "dark", "auto"], SettingScopes.User, visibleToShell: true);

    public static readonly Setting<string> Density = Setting.Choice(
        "shell.density", "How closely grids and record pages set their rows: comfortable, or compact for more on the screen.", "comfortable", ["comfortable", "compact"], SettingScopes.User, visibleToShell: true);

    /// <summary>
    /// The person's time zone for every time the shell shows, with its name (TF-065 AC2, ADR-0050): an IANA name such as
    /// <c>Asia/Kolkata</c>, or empty to follow the browser. The server stores and compares UTC whatever it says.
    /// </summary>
    public static readonly Setting<string> TimeZone = Setting.Text(
        "shell.time_zone",
        "The time zone every time in the shell is shown in, with its name; empty follows the browser.",
        string.Empty,
        SettingScopes.User,
        SettingRule.Matches("^([A-Za-z]+(/[A-Za-z0-9_+-]+)*)?$", "a time zone's name, such as Europe/London, or nothing to follow the browser"),
        visibleToShell: true);

    public static IReadOnlyList<SettingDefinition> All => [ColourScheme, Density, TimeZone];
}
