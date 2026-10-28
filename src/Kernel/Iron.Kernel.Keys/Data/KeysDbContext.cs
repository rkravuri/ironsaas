using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Encryption;
using Iron.Kernel.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Keys.Data;

/// <summary>
/// A key of the tenant as the control plane last answered (ADR-0054): its versions, its last rotation and its next
/// scheduled one. The worker writes it each time it asks; the Keys page reads it.
/// </summary>
public sealed class KeyState : AuditedEntity, IOwnAccessRule
{
    public const int KeyMaxLength = 20;

    private KeyState()
    {
        Key = string.Empty;
    }

    public KeyState(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
    }

    /// <summary><c>data</c>, <c>index</c>, <c>signing</c> or <c>database</c>.</summary>
    public string Key { get; private set; }

    public int KeyVersion { get; private set; }

    public int MinimumVersion { get; private set; }

    public DateTimeOffset? RotatedAt { get; private set; }

    public DateTimeOffset NextRotationAt { get; private set; }

    public int RotationDays { get; private set; }

    [NotTracked]
    public DateTimeOffset AskedAt { get; private set; }

    public void Answered(int version, int minimumVersion, DateTimeOffset? rotatedAt, DateTimeOffset nextRotationAt, int rotationDays, DateTimeOffset at)
    {
        KeyVersion = version;
        MinimumVersion = minimumVersion;
        RotatedAt = rotatedAt;
        NextRotationAt = nextRotationAt;
        RotationDays = rotationDays;
        AskedAt = at;
    }
}

/// <summary>
/// One rotation of the tenant's key or database password as the unit recorded it (TF-075, ADR-0054): who asked and why,
/// the versions, the re-encryption's progress here, and when the old version was retired. Its id is the control plane's.
/// </summary>
public sealed class KeyRotationEntry : AuditedEntity, IOwnAccessRule
{
    public const int ReasonMaxLength = 500;
    public const int NameMaxLength = 200;

    private KeyRotationEntry()
    {
        Key = string.Empty;
        Trigger = string.Empty;
        RequestedBy = string.Empty;
        Reason = string.Empty;
    }

    public KeyRotationEntry(Guid id, string key, string trigger, string requestedBy, string reason, int fromVersion, int toVersion, DateTimeOffset rotatedAt)
        : base(id)
    {
        Key = key;
        Trigger = trigger;
        RequestedBy = requestedBy.Length > NameMaxLength ? requestedBy[..NameMaxLength] : requestedBy;
        Reason = reason.Length > ReasonMaxLength ? reason[..ReasonMaxLength] : reason;
        FromVersion = fromVersion;
        ToVersion = toVersion;
        RotatedAt = rotatedAt;
    }

    public string Key { get; private set; }

    public string Trigger { get; private set; }

    public string RequestedBy { get; private set; }

    public string Reason { get; private set; }

    public int FromVersion { get; private set; }

    public int ToVersion { get; private set; }

    public DateTimeOffset RotatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public long Done { get; private set; }

    public long Total { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public DateTimeOffset? RetiredAt { get; private set; }

    /// <summary>When the control plane heard that every value moved; until then each pass tells it again.</summary>
    [NotTracked]
    public DateTimeOffset? FinishReportedAt { get; private set; }

    public void Started(DateTimeOffset at) => StartedAt ??= at;

    public void FinishReported(DateTimeOffset at) => FinishReportedAt ??= at;

    public void Progress(long done, long total)
    {
        Done = done;
        Total = Math.Max(done, total);
    }

    public void Finish(DateTimeOffset at) => FinishedAt ??= at;

    public void Retire(DateTimeOffset at)
    {
        FinishedAt ??= at;
        RetiredAt ??= at;
    }
}

/// <summary>
/// A key check (ADR-0054): a value the tenant stores, encrypted with its data key and found by its exact value through
/// its keyed hash, so an operator or the smoke proves after a rotation that the tenant's keys still read and find.
/// </summary>
public sealed class KeyCheck : AuditedEntity, IOwnAccessRule, ITrackedChanges
{
    public const string RecordType = "key_check";
    public const int LabelMaxLength = 100;
    public const int ValueMaxLength = 200;

    private KeyCheck()
    {
        Label = string.Empty;
    }

    public KeyCheck(string label, SearchableValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(value);
        Label = label;
        Value = value.Value;
        ValueHash = value.Hash;
    }

    public string Label { get; private set; }

    [Searchable]
    public EncryptedValue? Value { get; private set; }

    public SearchHash? ValueHash { get; private set; }
}

/// <summary>The <c>kernel_keys</c> schema: the tenant's keys as the control plane answered, its rotations, and its key checks.</summary>
[Schema("kernel_keys")]
public sealed class KeysDbContext(DbContextOptions<KeysDbContext> options) : ModuleDbContext(options)
{
    public DbSet<KeyState> States => Set<KeyState>();

    public DbSet<KeyRotationEntry> Rotations => Set<KeyRotationEntry>();

    public DbSet<KeyCheck> Checks => Set<KeyCheck>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KeyState>(state =>
        {
            state.ToTable("key_states");
            state.Property(s => s.Key).HasMaxLength(KeyState.KeyMaxLength);
            state.HasIndex(s => s.Key).IsUnique();
        });

        modelBuilder.Entity<KeyRotationEntry>(rotation =>
        {
            rotation.ToTable("key_rotations");
            rotation.Property(r => r.Key).HasMaxLength(KeyState.KeyMaxLength);
            rotation.Property(r => r.Trigger).HasMaxLength(20);
            rotation.Property(r => r.RequestedBy).HasMaxLength(KeyRotationEntry.NameMaxLength);
            rotation.Property(r => r.Reason).HasMaxLength(KeyRotationEntry.ReasonMaxLength);
            rotation.HasIndex(r => new { r.Key, r.RotatedAt });
        });

        modelBuilder.Entity<KeyCheck>(check =>
        {
            check.ToTable("key_checks");
            check.Property(c => c.Label).HasMaxLength(KeyCheck.LabelMaxLength);
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class KeysDbContextFactory : IDesignTimeDbContextFactory<KeysDbContext>
{
    public KeysDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<KeysDbContext>());
}
