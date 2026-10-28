using Iron.Kernel.History;

namespace Iron.Kernel.Keys.Contracts;

public static class KeysPermissions
{
    /// <summary>The tenant's keys as its administrators see them on the Keys page: versions, rotations and re-encryption (ADR-0054). Rotation is the operator's.</summary>
    public static readonly Permission Read = new("keys.keys.read", "See the tenant's keys: their versions, rotations, schedule and the running re-encryption", Risk.Low);

    /// <summary>Key checks: a value the tenant stores to find again by its exact value, which proves its keys still read and find after a rotation.</summary>
    public static readonly Permission ChecksManage = new("keys.checks.manage", "Store key checks and find them by their exact value", Risk.Medium);

    public static IReadOnlyList<Permission> All => [Read, ChecksManage];
}

public static class KeysAuditEvents
{
    /// <summary>A key or the database password was rotated in the control plane; the event names who asked and why (TF-075 AC3).</summary>
    public const string KeyRotated = "keys.key_rotated";

    /// <summary>Every value of the tenant moved to the rotated key's new version (TF-075 AC1).</summary>
    public const string ReencryptionFinished = "keys.reencryption_finished";

    /// <summary>The old versions of a key were retired: they decrypt, hash and verify nothing again (TF-075 AC1, AC2).</summary>
    public const string KeyRetired = "keys.key_retired";

    public const string CheckStored = "keys.check_stored";

    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        new AuditEventType(KeyRotated, "Key rotated").AsAdministrative(),
        new AuditEventType(ReencryptionFinished, "Values moved to the new key version").AsAdministrative(),
        new AuditEventType(KeyRetired, "Old key versions retired").AsAdministrative(),
        new AuditEventType(CheckStored, "Key check stored"),
    ];
}

public static class KeysSettings
{
    /// <summary>
    /// The tenant's policy for its data and index keys (TF-075, ADR-0054): the days between their scheduled rotations,
    /// which the control plane keeps within the operator's bounds. Yearly unless the tenant says otherwise.
    /// </summary>
    public static readonly Setting<int> DataRotationDays = Setting.WholeNumber(
        "keys.rotation.data_days",
        "How many days apart the tenant's data and index keys rotate on their schedule. The operator keeps it within their bounds.",
        365,
        30,
        730,
        SettingScopes.Tenant)
        .RequiringRecentAuthentication();

    public static IReadOnlyList<SettingDefinition> All => [DataRotationDays];
}

public static class KeysErrors
{
    public const string CheckLabelRequired = "keys.check.label_required";
    public const string CheckValueRequired = "keys.check.value_required";
}
