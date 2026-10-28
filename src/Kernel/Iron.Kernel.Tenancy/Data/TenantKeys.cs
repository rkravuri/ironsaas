using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Tenancy.Data;

/// <summary>
/// A tenant's key or database password as the control plane keeps it (ADR-0054): its latest and minimum versions, when it
/// last rotated, and how many days apart its scheduled rotations are. The secrets step records it when it makes the key.
/// </summary>
public sealed class TenantKeyRecord : AuditedEntity, ITenantWide
{
    public const int KeyMaxLength = 20;

    private TenantKeyRecord()
    {
        TenantSlug = string.Empty;
        Key = string.Empty;
    }

    public TenantKeyRecord(string tenantSlug, string key, int version, int minimumVersion, int rotationDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        TenantSlug = tenantSlug;
        Key = key;
        KeyVersion = Math.Max(1, version);
        MinimumVersion = Math.Max(1, minimumVersion);
        RotationDays = rotationDays;
    }

    public string TenantSlug { get; private set; }

    /// <summary>One of <c>TenantKeyNames</c>.</summary>
    public string Key { get; private set; }

    /// <summary>The latest version, which encrypts, hashes and signs; for the database password, how many it has had.</summary>
    public int KeyVersion { get; private set; }

    public int MinimumVersion { get; private set; }

    /// <summary>When it was last rotated; null while it has its first version.</summary>
    public DateTimeOffset? RotatedAt { get; private set; }

    /// <summary>Days between scheduled rotations: the tenant's policy within the operator's bounds, or the operator's own.</summary>
    public int RotationDays { get; private set; }

    /// <summary>When the schedule rotates it next: its last rotation, or its making, plus its days.</summary>
    public DateTimeOffset NextRotationAt => (RotatedAt ?? CreatedAt).AddDays(RotationDays);

    public void Versions(int version, int minimumVersion)
    {
        KeyVersion = Math.Max(1, version);
        MinimumVersion = Math.Max(1, minimumVersion);
    }

    public void Rotated(int version, DateTimeOffset at)
    {
        KeyVersion = version;
        RotatedAt = at;
    }

    public void Retired(int minimumVersion) => MinimumVersion = minimumVersion;

    public void Every(int days) => RotationDays = days;
}

/// <summary>
/// One rotation of a tenant's key or database password (TF-075, ADR-0054): who asked and why, the versions, and how far it
/// has come, the re-encryption's count as the unit reports it, until the old version is retired.
/// </summary>
public sealed class KeyRotationRecord : AuditedEntity, ITenantWide
{
    public const int ReasonMaxLength = 500;
    public const int NameMaxLength = 200;
    public const int ErrorMaxLength = 1000;

    private KeyRotationRecord()
    {
        TenantSlug = string.Empty;
        Key = string.Empty;
        Trigger = string.Empty;
        RequestedByName = string.Empty;
        Reason = string.Empty;
        State = string.Empty;
    }

    public KeyRotationRecord(string tenantSlug, string key, string trigger, Guid? requestedById, string requestedByName, string reason, string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        TenantSlug = tenantSlug;
        Key = key;
        Trigger = trigger;
        RequestedById = requestedById;
        RequestedByName = requestedByName.Length > NameMaxLength ? requestedByName[..NameMaxLength] : requestedByName;
        Reason = reason;
        State = state;
    }

    public string TenantSlug { get; private set; }

    public string Key { get; private set; }

    /// <summary>One of <c>KeyRotationTriggers</c>.</summary>
    public string Trigger { get; private set; }

    public Guid? RequestedById { get; private set; }

    public string RequestedByName { get; private set; }

    public string Reason { get; private set; }

    /// <summary>One of <c>KeyRotationStates</c>.</summary>
    public string State { get; private set; }

    public int? FromVersion { get; private set; }

    public int? ToVersion { get; private set; }

    public DateTimeOffset? RotatedAt { get; private set; }

    /// <summary>Values the unit's re-encryption moved so far.</summary>
    public long Done { get; private set; }

    /// <summary>Values the unit's re-encryption had to move when it started.</summary>
    public long Total { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public DateTimeOffset? RetiredAt { get; private set; }

    public string? Error { get; private set; }

    public void Rotate(int fromVersion, int toVersion, DateTimeOffset at)
    {
        FromVersion = fromVersion;
        ToVersion = toVersion;
        RotatedAt = at;
        State = Contracts.KeyRotationStates.Rotated;
    }

    public void Progress(long done, long total)
    {
        Done = Math.Max(0, done);
        Total = Math.Max(Done, total);
    }

    public void Finish(DateTimeOffset at)
    {
        FinishedAt = at;
        State = Contracts.KeyRotationStates.Finished;
    }

    public void Retire(DateTimeOffset at)
    {
        FinishedAt ??= at;
        RetiredAt = at;
        State = Contracts.KeyRotationStates.Retired;
    }

    public void Fail(string error)
    {
        Error = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
        State = Contracts.KeyRotationStates.Failed;
    }
}
