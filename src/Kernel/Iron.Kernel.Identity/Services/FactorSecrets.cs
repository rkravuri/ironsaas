using System.Security.Cryptography;
using Iron.Kernel.Data.Encryption;
using Iron.Kernel.Data.Seeding;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// The second factors' secrets at rest (TF-073, TF-074, ADR-0047, ADR-0054): encrypted with the tenant's data key through
/// the key service, so the unit holds no key of its own and a rotation's job moves them with every other encrypted field.
/// The person's id is sealed inside with the secret, so a secret copied onto another person's row does not open.
/// </summary>
public sealed class FactorSecrets(IFieldEncryption encryption)
{
    private const int IdLength = 16;

    public async Task<EncryptedValue> SealAsync(byte[] secret, Guid principalId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var plaintext = new byte[IdLength + secret.Length];
        try
        {
            principalId.TryWriteBytes(plaintext);
            secret.CopyTo(plaintext, IdLength);
            return await encryption.EncryptAsync(plaintext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<byte[]> OpenAsync(EncryptedValue sealedSecret, Guid principalId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sealedSecret);
        var plaintext = await encryption.DecryptBytesAsync(sealedSecret, cancellationToken).ConfigureAwait(false);
        try
        {
            if (plaintext.Length < IdLength || !CryptographicOperations.FixedTimeEquals(plaintext.AsSpan(0, IdLength), principalId.ToByteArray()))
            {
                throw new CryptographicException("The factor secret belongs to another person.");
            }

            return plaintext[IdLength..];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

/// <summary>
/// The factor key of T3 (ADR-0047), retired by ADR-0054: AES-256-GCM under 32 bytes the unit kept in its secret. It only
/// opens the secrets sealed before the key service, once, for <see cref="FactorSecretMove"/>; nothing is sealed with it.
/// </summary>
public sealed class LegacyFactorKey
{
    private const byte Version = 1;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private readonly byte[]? _key;

    public LegacyFactorKey(IdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.LegacyFactorKey))
        {
            return;
        }

        var key = Convert.FromBase64String(options.LegacyFactorKey.Trim());
        _key = key.Length == 32 ? key : throw new InvalidOperationException($"Identity:LegacyFactorKey holds {key.Length} bytes; the retired factor key held 32.");
    }

    public bool IsAvailable => _key is not null;

    public byte[] Open(byte[] sealedSecret, Guid principalId)
    {
        ArgumentNullException.ThrowIfNull(sealedSecret);
        if (_key is null || sealedSecret.Length < 1 + NonceLength + TagLength || sealedSecret[0] != Version)
        {
            throw new CryptographicException("The sealed factor secret has an unknown form, or the retired factor key is not given.");
        }

        var secret = new byte[sealedSecret.Length - 1 - NonceLength - TagLength];
        using var aes = new AesGcm(_key, TagLength);
        aes.Decrypt(sealedSecret.AsSpan(1, NonceLength), sealedSecret.AsSpan(1 + NonceLength + TagLength), sealedSecret.AsSpan(1 + NonceLength, TagLength), secret, principalId.ToByteArray());
        return secret;
    }
}

/// <summary>
/// Moves the authenticator apps' secrets sealed under the retired factor key onto the key service (ADR-0054), at the seed
/// the update after T9b runs. The fleet gives the seed the old key once, as <c>Identity:LegacyFactorKey</c>, from where it
/// kept it when it took the key out of the unit's secret. A secret it cannot open stops the seed, so the fleet keeps the
/// key and the next update tries again; without the key, the secrets left are named in the log, and their people reset
/// their authenticator app as after a lost store (ADR-0047).
/// </summary>
public sealed class FactorSecretMove(IdentityDbContext context, LegacyFactorKey legacy, FactorSecrets secrets, ILogger<FactorSecretMove> logger) : ISeeder
{
    // After the system roles and the first administrator, which nothing here depends on.
    public int Order => 900;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var sealedBefore = await context.Factors.Where(f => f.SecretCiphertext != null).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (sealedBefore.Count == 0)
        {
            return;
        }

        if (!legacy.IsAvailable)
        {
            logger.LogWarning("{Count} authenticator app secrets are sealed with the retired factor key, which this run was not given; their people reset their authenticator app.", sealedBefore.Count);
            return;
        }

        foreach (var factor in sealedBefore)
        {
            var secret = legacy.Open(factor.SecretCiphertext!, factor.PrincipalId);
            try
            {
                factor.MoveSecret(await secrets.SealAsync(secret, factor.PrincipalId, cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Moved {Count} authenticator app secrets onto the key service.", sealedBefore.Count);
    }
}
