using System.Security.Cryptography;
using Iron.Kernel.Data.Encryption;

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
