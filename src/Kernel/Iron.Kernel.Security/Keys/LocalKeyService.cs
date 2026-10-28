using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Security.Keys;

/// <summary>
/// The key service inside the process, for the tests and for a host run from source without a secret store (ADR-0054).
/// It keeps the transit engine's rules: versions per key, the latest encrypts, hashes and signs, retired versions do
/// nothing, and every value carries its version (<c>local:v&lt;n&gt;:...</c>). Encryption and hash keys are derived from
/// one material by HKDF with the key's name and version, so a host run from source reads its values after a restart;
/// signing keys too, as a private scalar the public key is computed from, so hosts that share a material sign alike.
/// Never for a unit with a store: there the keys live in the key service and nowhere else.
/// </summary>
public sealed class LocalKeyService : IKeyService, IKeyAdministration, IDisposable
{
    private const string Marker = "local";
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly byte[] _material;
    private readonly string _prefix;
    private readonly ConcurrentDictionary<string, LocalKey> _keys = new(StringComparer.Ordinal);

    public LocalKeyService(KeyOptions options, ILogger<LocalKeyService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _prefix = string.IsNullOrWhiteSpace(options.Prefix) ? "local" : options.Prefix.Trim();
        _material = Material(options.Local, logger);
    }

    public Task<IReadOnlyList<string>> EncryptAsync(KeyPurpose purpose, IReadOnlyList<byte[]> plaintexts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plaintexts);
        var key = Key(purpose);
        var version = key.Latest;
        IReadOnlyList<string> sealedValues = [.. plaintexts.Select(p => Seal(KeyOf(key.Name, version), version, p))];
        return Task.FromResult(sealedValues);
    }

    public Task<IReadOnlyList<byte[]>> DecryptAsync(KeyPurpose purpose, IReadOnlyList<string> ciphertexts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciphertexts);
        var key = Key(purpose);
        IReadOnlyList<byte[]> opened = [.. ciphertexts.Select(c => Open(key, c))];
        return Task.FromResult(opened);
    }

    public Task<IReadOnlyList<string>> RewrapAsync(KeyPurpose purpose, IReadOnlyList<string> ciphertexts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciphertexts);
        var key = Key(purpose);
        var version = key.Latest;
        IReadOnlyList<string> rewrapped = [.. ciphertexts.Select(c => Seal(KeyOf(key.Name, version), version, Open(key, c)))];
        return Task.FromResult(rewrapped);
    }

    public Task<IReadOnlyList<string>> HashAsync(KeyPurpose purpose, IReadOnlyList<byte[]> inputs, int? version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var key = Key(purpose);
        var chosen = Live(key, version ?? key.Latest);
        var hashKey = KeyOf(key.Name + "/hmac", chosen);
        IReadOnlyList<string> hashes = [.. inputs.Select(i => Form(chosen, HMACSHA256.HashData(hashKey, i)))];
        return Task.FromResult(hashes);
    }

    public Task<KeySignature> SignAsync(KeyPurpose purpose, byte[] data, int? version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        var key = Key(purpose);
        var chosen = Live(key, version ?? key.Latest);
        return Task.FromResult(new KeySignature(chosen, key.Signer(chosen).SignData(data, HashAlgorithmName.SHA256)));
    }

    public Task<KeyVersions> VersionsAsync(KeyPurpose purpose, CancellationToken cancellationToken) => Task.FromResult(Describe(Key(purpose), purpose == KeyPurpose.Signing));

    public Task EnsureAsync(string keyName, KeyKind kind, CancellationToken cancellationToken)
    {
        _keys.GetOrAdd(keyName, NewKey);
        return Task.CompletedTask;
    }

    public Task<KeyVersions?> DescribeAsync(string keyName, CancellationToken cancellationToken) =>
        Task.FromResult(_keys.TryGetValue(keyName, out var key) ? Describe(key, key.HasSigners) : null);

    public Task<KeyVersions> RotateAsync(string keyName, CancellationToken cancellationToken)
    {
        var key = _keys.GetOrAdd(keyName, NewKey);
        key.Rotate();
        return Task.FromResult(Describe(key, key.HasSigners));
    }

    public Task<KeyVersions> RetireAsync(string keyName, int minimumVersion, CancellationToken cancellationToken)
    {
        var key = _keys.GetOrAdd(keyName, NewKey);
        key.Retire(minimumVersion);
        return Task.FromResult(Describe(key, key.HasSigners));
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values)
        {
            key.Dispose();
        }
    }

    private LocalKey Key(KeyPurpose purpose) => _keys.GetOrAdd(KeyPurposes.KeyName(_prefix, purpose), NewKey);

    private LocalKey NewKey(string name) => new(name, version =>
    {
        // A P-256 private scalar from the material: below 2^255, so below the curve's order; .NET computes the public key.
        var scalar = KeyOf(name + "/ecdsa", version);
        scalar[0] &= 0x7f;
        return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = scalar });
    });

    private static KeyVersions Describe(LocalKey key, bool signing) =>
        new(key.Latest, key.Minimum, signing ? key.Live().ToDictionary(v => v, v => key.Signer(v).ExportSubjectPublicKeyInfoPem()) : null);

    private static int Live(LocalKey key, int version) =>
        version >= key.Minimum && version <= key.Latest ? version : throw new KeyVersionRetiredException($"Version {version} of '{key.Name}' is retired or does not exist.");

    private byte[] KeyOf(string name, int version) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _material, 32, info: Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"iron-key|{name}|v{version}")));

    private static string Seal(byte[] key, int version, byte[] plaintext)
    {
        var sealedBytes = new byte[NonceLength + TagLength + plaintext.Length];
        var nonce = sealedBytes.AsSpan(0, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plaintext, sealedBytes.AsSpan(NonceLength + TagLength), sealedBytes.AsSpan(NonceLength, TagLength));
        return Form(version, sealedBytes);
    }

    private byte[] Open(LocalKey key, string ciphertext)
    {
        var version = KeyText.VersionOf(ciphertext) ?? throw new CryptographicException("The value is not in the key service's form.");
        Live(key, version);
        var sealedBytes = Convert.FromBase64String(ciphertext[(ciphertext.LastIndexOf(':') + 1)..]);
        var plaintext = new byte[sealedBytes.Length - NonceLength - TagLength];
        using var aes = new AesGcm(KeyOf(key.Name, version), TagLength);
        aes.Decrypt(sealedBytes.AsSpan(0, NonceLength), sealedBytes.AsSpan(NonceLength + TagLength), sealedBytes.AsSpan(NonceLength, TagLength), plaintext);
        return plaintext;
    }

    private static string Form(int version, byte[] bytes) => string.Create(CultureInfo.InvariantCulture, $"{Marker}:v{version}:{Convert.ToBase64String(bytes)}");

    private static byte[] Material(KeyOptions.LocalKeyOptions options, ILogger? logger)
    {
        if (!string.IsNullOrWhiteSpace(options.Material))
        {
            return Convert.FromBase64String(options.Material.Trim());
        }

        if (!string.IsNullOrWhiteSpace(options.MaterialPath))
        {
            if (File.Exists(options.MaterialPath))
            {
                return Convert.FromBase64String(File.ReadAllText(options.MaterialPath).Trim());
            }

            var made = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.MaterialPath))!);
            File.WriteAllText(options.MaterialPath, Convert.ToBase64String(made));
            logger?.LogWarning("No key material at {Path}; new material was written there for this host run from source.", options.MaterialPath);
            return made;
        }

        return RandomNumberGenerator.GetBytes(32);
    }

    private sealed class LocalKey(string name, Func<int, ECDsa> signerOf) : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<int, ECDsa> _signers = [];

        public string Name { get; } = name;

        public int Latest { get; private set; } = 1;

        public int Minimum { get; private set; } = 1;

        public bool HasSigners
        {
            get
            {
                lock (_gate)
                {
                    return _signers.Count > 0;
                }
            }
        }

        public IEnumerable<int> Live() => Enumerable.Range(Minimum, Latest - Minimum + 1);

        public void Rotate()
        {
            lock (_gate)
            {
                Latest++;
            }
        }

        public void Retire(int minimum)
        {
            lock (_gate)
            {
                Minimum = Math.Clamp(minimum, Minimum, Latest);
                foreach (var version in _signers.Keys.Where(v => v < Minimum).ToList())
                {
                    _signers[version].Dispose();
                    _signers.Remove(version);
                }
            }
        }

        public ECDsa Signer(int version)
        {
            lock (_gate)
            {
                if (!_signers.TryGetValue(version, out var signer))
                {
                    signer = signerOf(version);
                    _signers[version] = signer;
                }

                return signer;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var signer in _signers.Values)
                {
                    signer.Dispose();
                }

                _signers.Clear();
            }
        }
    }
}
