using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// The unit's token signing key (ES256): from configuration, from a file written on first start, or ephemeral with a
/// warning. One key in S1; rotation keeps the previous public keys for validation (TF-020 AC3) and arrives with the
/// secret store in T12. Singleton.
/// </summary>
public sealed class SigningKeys : IDisposable
{
    private readonly ECDsa _privateKey;

    public SigningKeys(TokenOptions options, ILogger<SigningKeys> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (!string.IsNullOrWhiteSpace(options.SigningKeyPem))
        {
            _privateKey = Import(options.SigningKeyPem);
        }
        else if (!string.IsNullOrWhiteSpace(options.SigningKeyPath))
        {
            if (File.Exists(options.SigningKeyPath))
            {
                _privateKey = Import(File.ReadAllText(options.SigningKeyPath));
            }
            else
            {
                _privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.SigningKeyPath))!);
                File.WriteAllText(options.SigningKeyPath, _privateKey.ExportECPrivateKeyPem());
                logger.LogWarning("No signing key at {Path}; a new one was created there.", options.SigningKeyPath);
            }
        }
        else
        {
            _privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            logger.LogWarning("No signing key is configured (Tokens:SigningKeyPem or Tokens:SigningKeyPath); tokens are signed with a key that lives only as long as this process.");
        }

        KeyId = ComputeKeyId(_privateKey);

        // The library's default factory caches signature providers process-wide by key id. Two SigningKeys with the same
        // key (tests, a restart in the same process) would then share a provider over a key one of them disposed. A
        // factory of our own without the cache creates a provider per operation, which is cheap for ES256.
        CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false };
        SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(_privateKey) { KeyId = KeyId, CryptoProviderFactory = CryptoProviderFactory }, SecurityAlgorithms.EcdsaSha256)
        {
            CryptoProviderFactory = CryptoProviderFactory,
        };
        ValidationKeys = [new ECDsaSecurityKey(_privateKey) { KeyId = KeyId, CryptoProviderFactory = CryptoProviderFactory }];
    }

    public string KeyId { get; }

    /// <summary>Creates the signature providers for this key; validation must use it too.</summary>
    public CryptoProviderFactory CryptoProviderFactory { get; }

    public SigningCredentials SigningCredentials { get; }

    /// <summary>The keys a token may be signed with: the current one, and previous ones after a rotation.</summary>
    public IReadOnlyList<SecurityKey> ValidationKeys { get; }

    /// <summary>The public keys as a JSON Web Key Set, for <c>/.well-known/jwks.json</c>.</summary>
    public object JsonWebKeySet()
    {
        var parameters = _privateKey.ExportParameters(includePrivateParameters: false);
        return new
        {
            keys = new[]
            {
                new
                {
                    kty = "EC",
                    crv = "P-256",
                    use = "sig",
                    alg = "ES256",
                    kid = KeyId,
                    x = Base64UrlEncoder.Encode(parameters.Q.X!),
                    y = Base64UrlEncoder.Encode(parameters.Q.Y!),
                },
            },
        };
    }

    public void Dispose() => _privateKey.Dispose();

    private static ECDsa Import(string pem)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }

    private static string ComputeKeyId(ECDsa key)
    {
        var thumbprint = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        return Base64UrlEncoder.Encode(thumbprint[..12]);
    }
}
