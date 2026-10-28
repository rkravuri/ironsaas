using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Iron.Kernel.Identity.Services;

/// <summary>One public key of the set, its members named as RFC 7517 names them.</summary>
public sealed record JsonWebKeyResponse(string Kty, string Crv, string Use, string Alg, string Kid, string X, string Y);

/// <summary>The JSON Web Key Set that <c>/.well-known/jwks.json</c> answers (RFC 7517 section 5).</summary>
public sealed record JsonWebKeySetResponse(IReadOnlyList<JsonWebKeyResponse> Keys);

/// <summary>A live version of the signing key as tokens name it: its version, its key id and its public key.</summary>
public sealed record SigningKeyVersion(int Version, string KeyId, ECDsaSecurityKey Key, JsonWebKeyResponse Json);

/// <summary>
/// The unit's token signing key (ES256, TF-020, TF-075 AC2, ADR-0054), held by the key service: the latest version signs,
/// and every live version is published and validates, so a token signed before a rotation stays valid until it expires;
/// the fleet retires the old version only after every token it signed has. The public keys are read again once a minute,
/// and at once when a token names a key not seen yet. Singleton.
/// </summary>
public sealed class SigningKeys(IKeyService keys, TimeProvider time)
{
    /// <summary>How long the public keys are kept before they are read again.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    // An unknown key id reads the keys again at most this often, so tokens with made-up ids cannot hammer the store.
    private static readonly TimeSpan UnknownKeyPause = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<SigningKeyVersion> _versions = [];
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Creates the signature providers for these keys; validation must use it too. The library's default factory caches
    /// providers process-wide by key id, which would keep a provider over a key read again; one without the cache creates
    /// a provider per operation, which is cheap for ES256.
    /// </summary>
    public CryptoProviderFactory CryptoProviderFactory { get; } = new() { CacheSignatureProviders = false };

    /// <summary>The version that signs now, with its key id.</summary>
    public async Task<SigningKeyVersion> CurrentAsync(CancellationToken cancellationToken) =>
        (await VersionsAsync(force: false, cancellationToken).ConfigureAwait(false))[^1];

    /// <summary>The key service's signature of the data with the given version (r then s, as a JWS carries it).</summary>
    public async Task<byte[]> SignAsync(byte[] data, int version, CancellationToken cancellationToken) =>
        (await keys.SignAsync(KeyPurpose.Signing, data, version, cancellationToken).ConfigureAwait(false)).Signature;

    /// <summary>Every live version's key, for validation; read again first when <paramref name="keyId"/> is one not seen yet.</summary>
    public async Task<IReadOnlyList<SecurityKey>> ValidationKeysAsync(string? keyId, CancellationToken cancellationToken)
    {
        var versions = await VersionsAsync(force: false, cancellationToken).ConfigureAwait(false);
        if (keyId is not null && versions.All(v => v.KeyId != keyId) && time.GetUtcNow() - _readAt >= UnknownKeyPause)
        {
            versions = await VersionsAsync(force: true, cancellationToken).ConfigureAwait(false);
        }

        return [.. versions.Select(v => v.Key)];
    }

    /// <summary>The live public keys as a JSON Web Key Set, for <c>/.well-known/jwks.json</c>.</summary>
    public async Task<JsonWebKeySetResponse> JsonWebKeySetAsync(CancellationToken cancellationToken) =>
        new([.. (await VersionsAsync(force: false, cancellationToken).ConfigureAwait(false)).Select(v => v.Json)]);

    private async Task<IReadOnlyList<SigningKeyVersion>> VersionsAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && _versions.Count > 0 && time.GetUtcNow() - _readAt < RefreshInterval)
        {
            return _versions;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _versions.Count > 0 && time.GetUtcNow() - _readAt < RefreshInterval)
            {
                return _versions;
            }

            var described = await keys.VersionsAsync(KeyPurpose.Signing, cancellationToken).ConfigureAwait(false);
            var publicKeys = described.PublicKeys ?? throw new InvalidOperationException("The key service gave no public keys for the signing key.");
            _versions = [.. described.Live.Where(publicKeys.ContainsKey).Select(version => Version(version, publicKeys[version]))];
            if (_versions.Count == 0)
            {
                throw new InvalidOperationException("The signing key has no live version with a public key.");
            }

            _readAt = time.GetUtcNow();
            return _versions;
        }
        finally
        {
            _gate.Release();
        }
    }

    private SigningKeyVersion Version(int version, string publicKeyPem)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var keyId = Base64UrlEncoder.Encode(SHA256.HashData(key.ExportSubjectPublicKeyInfo())[..12]);
        var json = new JsonWebKeyResponse("EC", "P-256", "sig", "ES256", keyId, Base64UrlEncoder.Encode(parameters.Q.X!), Base64UrlEncoder.Encode(parameters.Q.Y!));
        return new SigningKeyVersion(version, keyId, new ECDsaSecurityKey(key) { KeyId = keyId, CryptoProviderFactory = CryptoProviderFactory }, json);
    }
}
