namespace Iron.Kernel.Tenancy.Contracts;

/// <summary>
/// What the control plane rotates for a tenant (TF-073, TF-075, ADR-0054): its three keys in the key service, named by
/// <see cref="KeyPurpose"/>, and the password its unit connects to its database with.
/// </summary>
public static class TenantKeyNames
{
    public const string Data = "data";
    public const string Index = "index";
    public const string Signing = "signing";
    public const string Database = "database";

    public static IReadOnlyList<string> All { get; } = [Data, Index, Signing, Database];

    public static bool IsKnown(string? key) => key is Data or Index or Signing or Database;

    /// <summary>The key service's purpose of a key; null for the database password.</summary>
    public static KeyPurpose? PurposeOf(string key) => KeyPurposes.TryParse(key, out var purpose) ? purpose : null;
}

/// <summary>Where a rotation stands: asked for, done in the key service, every value moved, the old version retired.</summary>
public static class KeyRotationStates
{
    public const string Requested = "requested";
    public const string Rotated = "rotated";
    public const string Finished = "finished";
    public const string Retired = "retired";
    public const string Failed = "failed";
}

/// <summary>Who asked for a rotation: an operator with the key officer permission, or the schedule.</summary>
public static class KeyRotationTriggers
{
    public const string Operator = "operator";
    public const string Schedule = "schedule";
}

/// <summary>A tenant's key or database password as the control plane keeps it.</summary>
public sealed record TenantKeyInfo(string TenantSlug, string Key, int Version, int MinimumVersion, DateTimeOffset? RotatedAt, DateTimeOffset NextRotationAt, int RotationDays);

/// <summary>
/// One rotation: what, who asked and why, and how far it has come; the re-encryption's count of values done and to do,
/// as the unit reports it.
/// </summary>
public sealed record KeyRotationInfo(
    Guid Id,
    string TenantSlug,
    string Key,
    string Trigger,
    string RequestedBy,
    string Reason,
    string State,
    int? FromVersion,
    int? ToVersion,
    DateTimeOffset RequestedAt,
    DateTimeOffset? RotatedAt,
    long Done,
    long Total,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? RetiredAt,
    string? Error);

/// <summary>
/// The tenants' keys as the fleet works on them (ADR-0054): the secrets step records the keys it made, the key worker
/// asks for the schedule's rotations, takes each requested rotation and records what it did, and retires an old version
/// once every value has moved or, for a signing key, once every token it signed has expired.
/// </summary>
public interface ITenantKeys
{
    /// <summary>Records a key's versions as the key service has them; a key recorded before keeps its rotation times.</summary>
    Task RecordKeyAsync(string slug, string key, int version, int minimumVersion, CancellationToken cancellationToken);

    /// <summary>Asks for a rotation of every key whose schedule is due, for active tenants; the number asked for.</summary>
    Task<int> ScheduleDueAsync(CancellationToken cancellationToken);

    /// <summary>The rotations asked for and not yet done, oldest first.</summary>
    Task<IReadOnlyList<KeyRotationInfo>> RequestedAsync(CancellationToken cancellationToken);

    /// <summary>The rotation is done in the key service: the new version encrypts, hashes and signs. A database password is finished at once.</summary>
    Task RotatedAsync(Guid rotationId, int fromVersion, int toVersion, CancellationToken cancellationToken);

    Task FailedAsync(Guid rotationId, string error, CancellationToken cancellationToken);

    /// <summary>
    /// The rotations whose old version may go now: data and index keys whose unit reported every value moved, and signing
    /// keys rotated longer ago than every token signed with the old version lives.
    /// </summary>
    Task<IReadOnlyList<KeyRotationInfo>> RetirableAsync(CancellationToken cancellationToken);

    /// <summary>The old versions are retired in the key service: nothing decrypts, hashes or verifies with them again.</summary>
    Task RetiredAsync(Guid rotationId, int minimumVersion, CancellationToken cancellationToken);
}
