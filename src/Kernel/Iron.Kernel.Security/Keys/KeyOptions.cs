namespace Iron.Kernel.Security.Keys;

/// <summary>
/// Bound from the <c>Keys</c> configuration section (ADR-0054). <see cref="Prefix"/> names this unit's key family in the
/// key service: <c>tenant-&lt;slug&gt;</c>, which the fleet writes into the unit's secret, or <c>control</c> for the control
/// plane. Without a secret store, the keys live in this process (<see cref="LocalKeyService"/>), from
/// <see cref="LocalKeyOptions.Material"/> or the file at <see cref="LocalKeyOptions.MaterialPath"/>.
/// </summary>
public sealed class KeyOptions
{
    public const string SectionName = "Keys";

    public string? Prefix { get; set; }

    public LocalKeyOptions Local { get; set; } = new();

    public sealed class LocalKeyOptions
    {
        /// <summary>32 random bytes in base64, from which each key version is derived; a host run from source and the tests only.</summary>
        public string? Material { get; set; }

        /// <summary>A file holding the material, written on first start when missing, so a host run from source keeps its keys across restarts.</summary>
        public string? MaterialPath { get; set; }
    }
}

/// <summary>What a key does in the key service, which decides its type there.</summary>
public enum KeyKind
{
    /// <summary>AES-256-GCM: encrypts, decrypts and re-wraps chosen fields.</summary>
    Encryption,

    /// <summary>HMAC-SHA256: the keyed hashes that find an encrypted field by its exact value.</summary>
    Hash,

    /// <summary>ECDSA P-256: signs access tokens; its HMAC signs the login's check.</summary>
    Signing,
}

public static class KeyKinds
{
    public static KeyKind Of(KeyPurpose purpose) => purpose switch
    {
        KeyPurpose.Data => KeyKind.Encryption,
        KeyPurpose.Index => KeyKind.Hash,
        KeyPurpose.Signing => KeyKind.Signing,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown key purpose."),
    };
}

/// <summary>
/// What the fleet does with keys and a unit never may (ADR-0054): make a tenant's keys, rotate them, and retire the old
/// versions once every value has moved. The unit's store token may use its own keys only, and none of these.
/// </summary>
public interface IKeyAdministration
{
    /// <summary>Makes the key when it does not exist; a key that exists is left as it is.</summary>
    Task EnsureAsync(string keyName, KeyKind kind, CancellationToken cancellationToken);

    /// <summary>The key's versions, or null when there is no such key.</summary>
    Task<KeyVersions?> DescribeAsync(string keyName, CancellationToken cancellationToken);

    /// <summary>Adds a new version, which encrypts, hashes and signs from now on; the older ones keep working until retired.</summary>
    Task<KeyVersions> RotateAsync(string keyName, CancellationToken cancellationToken);

    /// <summary>Retires every version below <paramref name="minimumVersion"/>: nothing decrypts, hashes or verifies with them again.</summary>
    Task<KeyVersions> RetireAsync(string keyName, int minimumVersion, CancellationToken cancellationToken);
}
