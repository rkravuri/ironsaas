using System.Security.Cryptography;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Encrypts the second factors' secrets at rest (TF-073, ADR-0047): AES-256-GCM under the unit's factor key, which comes
/// from the unit's secret store and nowhere else. A sealed secret is a version byte, a random 96-bit nonce, the 128-bit
/// tag and the ciphertext; the person's id is the associated data, so a secret copied onto another person's row does not
/// open. The version byte leaves room for a second key when the key is rotated. Without a key nothing is sealed: the host
/// refuses to save a factor rather than keep a secret in the clear.
/// </summary>
public sealed class FactorProtector
{
    private const byte Version = 1;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private readonly byte[]? _key;

    public FactorProtector(IdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.FactorKey))
        {
            return;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(options.FactorKey.Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Identity:FactorKey is not base64. It must be 32 random bytes in base64, from the unit's secret.", exception);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException($"Identity:FactorKey holds {key.Length} bytes; it must hold 32 (AES-256).");
        }

        _key = key;
    }

    public bool IsAvailable => _key is not null;

    public byte[] Seal(ReadOnlySpan<byte> secret, Guid principalId)
    {
        var key = Key();
        var sealedSecret = new byte[1 + NonceLength + TagLength + secret.Length];
        sealedSecret[0] = Version;
        var nonce = sealedSecret.AsSpan(1, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, secret, sealedSecret.AsSpan(1 + NonceLength + TagLength), sealedSecret.AsSpan(1 + NonceLength, TagLength), principalId.ToByteArray());
        return sealedSecret;
    }

    public byte[] Open(byte[] sealedSecret, Guid principalId)
    {
        ArgumentNullException.ThrowIfNull(sealedSecret);
        if (sealedSecret.Length < 1 + NonceLength + TagLength || sealedSecret[0] != Version)
        {
            throw new CryptographicException("The sealed factor secret has an unknown form.");
        }

        var key = Key();
        var secret = new byte[sealedSecret.Length - 1 - NonceLength - TagLength];
        using var aes = new AesGcm(key, TagLength);
        aes.Decrypt(sealedSecret.AsSpan(1, NonceLength), sealedSecret.AsSpan(1 + NonceLength + TagLength), sealedSecret.AsSpan(1 + NonceLength, TagLength), secret, principalId.ToByteArray());
        return secret;
    }

    private byte[] Key() => _key ?? throw new InvalidOperationException("Identity:FactorKey is not configured, so no factor secret can be sealed or opened. The fleet writes it into the unit's secret.");
}
