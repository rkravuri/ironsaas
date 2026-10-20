using System.Text.Json;
using System.Text.RegularExpressions;

namespace Iron.Kernel;

/// <summary>The value a flag holds (TF-050): a boolean, a string, a number or a JSON document.</summary>
public enum FlagType
{
    Boolean,
    String,
    Number,
    Json,
}

/// <summary>Why a flag exists (TF-052): to release, to experiment, to operate the platform, or to stand in for a permission.</summary>
public enum FlagKind
{
    Release,
    Experiment,
    Operational,
    PermissionLike,
}

/// <summary>
/// A feature flag as its module declares it in code (TF-050, TF-052, ADR-0035): key, type, kind, purpose, owner, planned
/// removal date and the default that applies when nothing is known. Rules are the operator's, in the control plane.
/// A declaration without an owner does not construct, so the build refuses it. The family <c>kill.&lt;path&gt;</c> is
/// reserved for kill switches (TF-051), which <see cref="KillSwitches"/> declares for the whole platform.
/// </summary>
public sealed partial record Flag
{
    public const string KillSwitchPrefix = "kill.";

    public Flag(string key, FlagType type, FlagKind kind, string purpose, string owner, DateOnly? removeBy, JsonElement defaultValue)
    {
        if (!IsValidKey(key))
        {
            throw new ArgumentException($"'{key}' is not a flag key. Use dotted lowercase words, for example authorization.import_preview.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException($"The flag '{key}' declares no owner. Every flag names the team or person who removes it (TF-052).", nameof(owner));
        }

        if (string.IsNullOrWhiteSpace(purpose))
        {
            throw new ArgumentException($"The flag '{key}' declares no purpose.", nameof(purpose));
        }

        if (!Fits(type, defaultValue))
        {
            throw new ArgumentException($"The default of the flag '{key}' is not a {type.ToString().ToLowerInvariant()} value.", nameof(defaultValue));
        }

        var killSwitch = key.StartsWith(KillSwitchPrefix, StringComparison.Ordinal);
        if (killSwitch && (type != FlagType.Boolean || kind != FlagKind.Operational || defaultValue.ValueKind != JsonValueKind.False))
        {
            throw new ArgumentException($"'{key}' is in the kill-switch family: a kill switch is an operational boolean that defaults to off.", nameof(key));
        }

        if (kind is FlagKind.Release or FlagKind.Experiment && removeBy is null)
        {
            throw new ArgumentException($"The {kind.ToString().ToLowerInvariant()} flag '{key}' declares no removal date (TF-052).", nameof(removeBy));
        }

        Key = key;
        Type = type;
        Kind = kind;
        Purpose = purpose.Trim();
        Owner = owner.Trim();
        RemoveBy = removeBy;
        Default = defaultValue.Clone();
    }

    public string Key { get; }

    public FlagType Type { get; }

    public FlagKind Kind { get; }

    public string Purpose { get; }

    /// <summary>The team or person who answers for the flag and removes it.</summary>
    public string Owner { get; }

    /// <summary>When the flag should be gone; none for an operational flag such as a kill switch.</summary>
    public DateOnly? RemoveBy { get; }

    /// <summary>The value when no rule applies, and when nothing is known at all (ADR-0035: last known values come first).</summary>
    public JsonElement Default { get; }

    public bool IsKillSwitch => Key.StartsWith(KillSwitchPrefix, StringComparison.Ordinal);

    /// <summary>The path a kill switch stops (<c>imports</c> for <c>kill.imports</c>), or null.</summary>
    public string? KillSwitchPath => IsKillSwitch ? Key[KillSwitchPrefix.Length..] : null;

    public static Flag Boolean(string key, FlagKind kind, string purpose, string owner, DateOnly? removeBy, bool defaultValue) =>
        new(key, FlagType.Boolean, kind, purpose, owner, removeBy, JsonSerializer.SerializeToElement(defaultValue));

    public static Flag String(string key, FlagKind kind, string purpose, string owner, DateOnly? removeBy, string defaultValue) =>
        new(key, FlagType.String, kind, purpose, owner, removeBy, JsonSerializer.SerializeToElement(defaultValue));

    public static Flag Number(string key, FlagKind kind, string purpose, string owner, DateOnly? removeBy, double defaultValue) =>
        new(key, FlagType.Number, kind, purpose, owner, removeBy, JsonSerializer.SerializeToElement(defaultValue));

    public static Flag Json(string key, FlagKind kind, string purpose, string owner, DateOnly? removeBy, string defaultJson) =>
        new(key, FlagType.Json, kind, purpose, owner, removeBy, JsonDocument.Parse(defaultJson).RootElement.Clone());

    /// <summary>A kill switch of the reserved family: an operational boolean, off unless an operator turns it on (TF-051).</summary>
    public static Flag KillSwitch(string path, string purpose) =>
        Boolean(KillSwitchPrefix + path, FlagKind.Operational, purpose, KillSwitches.Owner, null, false);

    public static bool IsValidKey(string? key) => key is not null && key.Length <= 100 && KeyPattern().IsMatch(key);

    /// <summary>Whether a value is of a flag type: JSON accepts anything.</summary>
    public static bool Fits(FlagType type, JsonElement value) => type switch
    {
        FlagType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        FlagType.String => value.ValueKind == JsonValueKind.String,
        FlagType.Number => value.ValueKind == JsonValueKind.Number,
        FlagType.Json => value.ValueKind != JsonValueKind.Undefined,
        _ => false,
    };

    public bool Equals(Flag? other) => other is not null && string.Equals(Key, other.Key, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>
/// The kill switches of the platform (TF-051, ADR-0035): one per critical path, declared by the kernel for every host,
/// off by default, flipped only by an operator with a reason. Imports are wired now; email, webhooks, SMS, AI calls and
/// live push are declared now and wired by the slices that build those paths.
/// </summary>
public static class KillSwitches
{
    public const string Owner = "Platform operations";

    public static readonly Flag Email = Flag.KillSwitch("email", "Stops outbound email; the messages wait and are retried when it is off (S4).");

    public static readonly Flag Webhooks = Flag.KillSwitch("webhooks", "Stops outbound webhook calls.");

    public static readonly Flag Sms = Flag.KillSwitch("sms", "Stops outbound SMS.");

    public static readonly Flag Ai = Flag.KillSwitch("ai", "Stops calls to AI services.");

    public static readonly Flag Push = Flag.KillSwitch("push", "Stops live push to browsers and devices.");

    public static readonly Flag Imports = Flag.KillSwitch("imports", "Stops imports into a tenant, role import among them.");

    public static IReadOnlyList<Flag> All => [Email, Webhooks, Sms, Ai, Push, Imports];
}
