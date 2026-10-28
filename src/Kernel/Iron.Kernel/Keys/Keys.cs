using System.Globalization;
using System.Security.Cryptography;

namespace Iron.Kernel;

/// <summary>
/// What a tenant's keys are for (TF-074, TF-075, ADR-0054). Each purpose is one named key in the key service, versioned:
/// <c>tenant-&lt;slug&gt;-data</c> encrypts chosen fields, <c>tenant-&lt;slug&gt;-index</c> makes the keyed hashes that find
/// them by exact value, and <c>tenant-&lt;slug&gt;-signing</c> signs access tokens and the login's check.
/// </summary>
public enum KeyPurpose
{
    Data,
    Index,
    Signing,
}

public static class KeyPurposes
{
    public static IReadOnlyList<KeyPurpose> All { get; } = [KeyPurpose.Data, KeyPurpose.Index, KeyPurpose.Signing];

    /// <summary>The purpose's word in a key's name, an API path and an audit event: <c>data</c>, <c>index</c>, <c>signing</c>.</summary>
    public static string Word(this KeyPurpose purpose) => purpose switch
    {
        KeyPurpose.Data => "data",
        KeyPurpose.Index => "index",
        KeyPurpose.Signing => "signing",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown key purpose."),
    };

    public static bool TryParse(string? word, out KeyPurpose purpose)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Word(), word, StringComparison.Ordinal))
            {
                purpose = candidate;
                return true;
            }
        }

        purpose = default;
        return false;
    }

    /// <summary>The key's name for a key family, such as <c>tenant-acme</c> or <c>control</c>: <c>tenant-acme-data</c>.</summary>
    public static string KeyName(string prefix, KeyPurpose purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return $"{prefix}-{purpose.Word()}";
    }

    /// <summary>A tenant's key family: <c>tenant-&lt;slug&gt;</c>.</summary>
    public static string TenantPrefix(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        return $"tenant-{slug}";
    }
}

/// <summary>
/// A key's versions as the key service keeps them (TF-075): the latest encrypts, signs and hashes; every version from
/// <paramref name="Minimum"/> up still decrypts, verifies and hashes for a search; older ones are retired and do neither.
/// A signing key also gives the public key of each live version, in PEM.
/// </summary>
public sealed record KeyVersions(int Latest, int Minimum, IReadOnlyDictionary<int, string>? PublicKeys = null)
{
    /// <summary>The versions that still work, oldest first.</summary>
    public IEnumerable<int> Live => Enumerable.Range(Minimum, Math.Max(0, Latest - Minimum + 1));
}

/// <summary>A signature the key service made: the version that signed, and the bytes (r then s for ECDSA P-256, as a JWS carries them).</summary>
public sealed record KeySignature(int Version, byte[] Signature);

/// <summary>
/// The one way the application encrypts, decrypts, hashes and signs with a tenant's keys (TF-074, TF-075, ADR-0054). On
/// the laptop OpenBao's transit engine holds the keys and does the work, so the application never holds a key; in Azure
/// Key Vault takes the same place. A value encrypted or hashed carries the version that made it, as
/// <c>&lt;service&gt;:v&lt;version&gt;:&lt;base64&gt;</c>, so a value is moved to a newer version without guessing.
/// Calls take lists so a page of values is one round trip.
/// </summary>
public interface IKeyService
{
    /// <summary>Each plaintext encrypted with the latest version of the key.</summary>
    Task<IReadOnlyList<string>> EncryptAsync(KeyPurpose purpose, IReadOnlyList<byte[]> plaintexts, CancellationToken cancellationToken);

    /// <summary>Each ciphertext decrypted; a ciphertext of a retired version throws <see cref="KeyVersionRetiredException"/>.</summary>
    Task<IReadOnlyList<byte[]>> DecryptAsync(KeyPurpose purpose, IReadOnlyList<string> ciphertexts, CancellationToken cancellationToken);

    /// <summary>Each ciphertext encrypted again with the latest version, inside the key service: the plaintext never leaves it.</summary>
    Task<IReadOnlyList<string>> RewrapAsync(KeyPurpose purpose, IReadOnlyList<string> ciphertexts, CancellationToken cancellationToken);

    /// <summary>The keyed hash (HMAC-SHA256) of each input with the given version, or the latest; a retired version throws <see cref="KeyVersionRetiredException"/>.</summary>
    Task<IReadOnlyList<string>> HashAsync(KeyPurpose purpose, IReadOnlyList<byte[]> inputs, int? version, CancellationToken cancellationToken);

    /// <summary>The data signed (ES256) with the given version, or the latest.</summary>
    Task<KeySignature> SignAsync(KeyPurpose purpose, byte[] data, int? version, CancellationToken cancellationToken);

    /// <summary>The key's latest and minimum versions, and a signing key's public keys.</summary>
    Task<KeyVersions> VersionsAsync(KeyPurpose purpose, CancellationToken cancellationToken);
}

public static class KeyServiceExtensions
{
    public static async Task<string> EncryptAsync(this IKeyService keys, KeyPurpose purpose, byte[] plaintext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return (await keys.EncryptAsync(purpose, [plaintext], cancellationToken).ConfigureAwait(false))[0];
    }

    public static async Task<byte[]> DecryptAsync(this IKeyService keys, KeyPurpose purpose, string ciphertext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return (await keys.DecryptAsync(purpose, [ciphertext], cancellationToken).ConfigureAwait(false))[0];
    }

    public static async Task<string> HashAsync(this IKeyService keys, KeyPurpose purpose, byte[] input, int? version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return (await keys.HashAsync(purpose, [input], version, cancellationToken).ConfigureAwait(false))[0];
    }
}

/// <summary>The form of a value the key service made: <c>&lt;service&gt;:v&lt;version&gt;:&lt;base64&gt;</c>.</summary>
public static class KeyText
{
    /// <summary>The version that encrypted or hashed the value; null when the text is not in the form.</summary>
    public static int? VersionOf(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var first = text.IndexOf(':', StringComparison.Ordinal);
        var second = first < 0 ? -1 : text.IndexOf(':', first + 1);
        if (second < 0 || second - first < 3 || text[first + 1] != 'v')
        {
            return null;
        }

        return int.TryParse(text.AsSpan(first + 2, second - first - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : null;
    }

    /// <summary>The marker of a version inside the form, <c>v3</c>, as the re-encryption compares it.</summary>
    public static string VersionMarker(int version) => string.Create(CultureInfo.InvariantCulture, $"v{version}");
}

/// <summary>
/// A value made with a version of a key that has been retired (TF-075 AC1): the key service no longer decrypts or hashes
/// with it. Every value is moved to the new version before a version is retired, so meeting one is a value the
/// re-encryption missed, or an old answer, such as a login check issued before the rotation and kept past its lifetime.
/// </summary>
public sealed class KeyVersionRetiredException : CryptographicException
{
    public KeyVersionRetiredException()
    {
    }

    public KeyVersionRetiredException(string message)
        : base(message)
    {
    }

    public KeyVersionRetiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
