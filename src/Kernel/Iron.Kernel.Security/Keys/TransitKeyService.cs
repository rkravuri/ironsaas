using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iron.Kernel.Security.Secrets;
using System.Buffers.Text;

namespace Iron.Kernel.Security.Keys;

/// <summary>
/// The key service on OpenBao's transit engine (ADR-0054): the keys stay in the store, which encrypts, decrypts, re-wraps,
/// hashes and signs, so the application never holds a key. Each call takes a list, one round trip for a page of values.
/// A unit's token may use its own tenant's keys and nothing else; the fleet's token also makes, rotates and retires them.
/// Over the transit engine's HTTP API, as <see cref="OpenBaoSecretStore"/> speaks KV, without a client library.
/// </summary>
public sealed class TransitKeyService : IKeyService, IKeyAdministration, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _mount;
    private readonly string? _prefix;

    public TransitKeyService(SecretStoreOptions store, KeyOptions keys)
        : this(store, keys, new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
    {
    }

    public TransitKeyService(SecretStoreOptions store, KeyOptions keys, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(http);
        if (!store.IsConfigured)
        {
            throw new InvalidOperationException("The key service needs Secrets:Address and Secrets:Token.");
        }

        _http = http;
        _http.BaseAddress = new Uri(store.Address!.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Remove("X-Vault-Token");
        _http.DefaultRequestHeaders.Add("X-Vault-Token", store.Token);
        _mount = store.TransitMount.Trim('/');
        _prefix = string.IsNullOrWhiteSpace(keys.Prefix) ? null : keys.Prefix.Trim();
    }

    public async Task<IReadOnlyList<string>> EncryptAsync(KeyPurpose purpose, IReadOnlyList<byte[]> plaintexts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plaintexts);
        if (plaintexts.Count == 0)
        {
            return [];
        }

        var items = new JsonArray([.. plaintexts.Select(p => (JsonNode)new JsonObject { ["plaintext"] = Convert.ToBase64String(p) })]);
        var results = await BatchAsync("encrypt", Name(purpose), new JsonObject { ["batch_input"] = items }, cancellationToken).ConfigureAwait(false);
        return [.. results.Select(r => Text(r, "ciphertext"))];
    }

    public async Task<IReadOnlyList<byte[]>> DecryptAsync(KeyPurpose purpose, IReadOnlyList<string> ciphertexts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciphertexts);
        if (ciphertexts.Count == 0)
        {
            return [];
        }

        var items = new JsonArray([.. ciphertexts.Select(c => (JsonNode)new JsonObject { ["ciphertext"] = c })]);
        var results = await BatchAsync("decrypt", Name(purpose), new JsonObject { ["batch_input"] = items }, cancellationToken).ConfigureAwait(false);
        return [.. results.Select(r => Convert.FromBase64String(Text(r, "plaintext")))];
    }

    public async Task<IReadOnlyList<string>> RewrapAsync(KeyPurpose purpose, IReadOnlyList<string> ciphertexts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciphertexts);
        if (ciphertexts.Count == 0)
        {
            return [];
        }

        var items = new JsonArray([.. ciphertexts.Select(c => (JsonNode)new JsonObject { ["ciphertext"] = c })]);
        var results = await BatchAsync("rewrap", Name(purpose), new JsonObject { ["batch_input"] = items }, cancellationToken).ConfigureAwait(false);
        return [.. results.Select(r => Text(r, "ciphertext"))];
    }

    public async Task<IReadOnlyList<string>> HashAsync(KeyPurpose purpose, IReadOnlyList<byte[]> inputs, int? version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            return [];
        }

        var body = new JsonObject { ["batch_input"] = new JsonArray([.. inputs.Select(i => (JsonNode)new JsonObject { ["input"] = Convert.ToBase64String(i) })]) };
        if (version is { } chosen)
        {
            body["key_version"] = chosen;
        }

        var results = await BatchAsync("hmac", Name(purpose), body, cancellationToken).ConfigureAwait(false);
        return [.. results.Select(r => Text(r, "hmac"))];
    }

    public async Task<KeySignature> SignAsync(KeyPurpose purpose, byte[] data, int? version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        var name = Name(purpose);
        var body = new JsonObject { ["input"] = Convert.ToBase64String(data), ["marshaling_algorithm"] = "jws", ["hash_algorithm"] = "sha2-256" };
        if (version is { } chosen)
        {
            body["key_version"] = chosen;
        }

        var answer = await SendAsync(HttpMethod.Post, $"sign/{name}", body, "sign with", name, cancellationToken).ConfigureAwait(false);
        var signature = Text(answer, "signature");
        var signedWith = KeyText.VersionOf(signature) ?? throw new SecretStoreException($"The key service answered a signature of no version for '{name}'.");
        return new KeySignature(signedWith, Base64Url.DecodeFromChars(signature.AsSpan(signature.LastIndexOf(':') + 1)));
    }

    public async Task<KeyVersions> VersionsAsync(KeyPurpose purpose, CancellationToken cancellationToken)
    {
        var name = Name(purpose);
        return await DescribeAsync(name, cancellationToken).ConfigureAwait(false) ?? throw new SecretStoreException($"The key service has no key '{name}': the fleet makes a tenant's keys when it provisions or updates the unit.");
    }

    public async Task EnsureAsync(string keyName, KeyKind kind, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        if (await DescribeAsync(keyName, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        var body = kind switch
        {
            KeyKind.Encryption => new JsonObject { ["type"] = "aes256-gcm96" },
            KeyKind.Hash => new JsonObject { ["type"] = "hmac", ["key_size"] = 32 },
            KeyKind.Signing => new JsonObject { ["type"] = "ecdsa-p256" },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown key kind."),
        };
        await SendAsync(HttpMethod.Post, $"keys/{keyName}", body, "make the key", keyName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KeyVersions?> DescribeAsync(string keyName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        using var response = await _http.GetAsync(new Uri($"v1/{_mount}/keys/{keyName}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return Versions(await ReadDataAsync(response, "read the key", keyName, cancellationToken).ConfigureAwait(false));
    }

    public async Task<KeyVersions> RotateAsync(string keyName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        await SendAsync(HttpMethod.Post, $"keys/{keyName}/rotate", new JsonObject(), "rotate the key", keyName, cancellationToken).ConfigureAwait(false);
        return await DescribeAsync(keyName, cancellationToken).ConfigureAwait(false) ?? throw new SecretStoreException($"The key '{keyName}' was rotated but cannot be read.");
    }

    public async Task<KeyVersions> RetireAsync(string keyName, int minimumVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumVersion, 1);
        await SendAsync(HttpMethod.Post, $"keys/{keyName}/config", new JsonObject { ["min_decryption_version"] = minimumVersion }, "retire the old versions of", keyName, cancellationToken).ConfigureAwait(false);
        return await DescribeAsync(keyName, cancellationToken).ConfigureAwait(false) ?? throw new SecretStoreException($"The key '{keyName}' was retired but cannot be read.");
    }

    public void Dispose() => _http.Dispose();

    private string Name(KeyPurpose purpose) =>
        KeyPurposes.KeyName(_prefix ?? throw new InvalidOperationException("Keys:Prefix is not configured: the fleet writes the unit's key family (tenant-<slug>) into its secret."), purpose);

    private async Task<IReadOnlyList<JsonNode>> BatchAsync(string operation, string name, JsonObject body, CancellationToken cancellationToken)
    {
        var data = await SendAsync(HttpMethod.Post, $"{operation}/{name}", body, operation + " with", name, cancellationToken).ConfigureAwait(false);
        var results = data["batch_results"]?.AsArray() ?? throw new SecretStoreException($"The key service answered no results to {operation} with '{name}'.");
        var answers = new List<JsonNode>(results.Count);
        foreach (var result in results)
        {
            if (result is null)
            {
                throw new SecretStoreException($"The key service answered an empty result to {operation} with '{name}'.");
            }

            if (result["error"]?.GetValue<string>() is { Length: > 0 } error)
            {
                throw Refusal(operation + " with", name, error);
            }

            answers.Add(result);
        }

        return answers;
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonObject body, string operation, string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri($"v1/{_mount}/{path}", UriKind.Relative)) { Content = JsonContent.Create(body) };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadDataAsync(response, operation, name, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonNode> ReadDataAsync(HttpResponseMessage response, string operation, string name, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Batch errors come back as the whole answer's errors too; the first says what went wrong.
            var errors = TryParse(text)?["errors"]?.AsArray().Select(e => e?.GetValue<string>()).FirstOrDefault(e => !string.IsNullOrEmpty(e));
            var batch = TryParse(text)?["data"]?["batch_results"]?.AsArray().Select(r => r?["error"]?.GetValue<string>()).FirstOrDefault(e => !string.IsNullOrEmpty(e));
            throw Refusal(operation, name, batch ?? errors ?? $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return TryParse(text)?["data"] ?? new JsonObject();
    }

    private static Exception Refusal(string operation, string name, string error) =>
        error.Contains("too old", StringComparison.OrdinalIgnoreCase) || error.Contains("no such key version", StringComparison.OrdinalIgnoreCase)
            ? new KeyVersionRetiredException($"The key service would not {operation} '{name}': that version of the key is retired.")
            : new SecretStoreException($"The key service refused to {operation} '{name}': {(error.Length > 300 ? error[..300] : error)}");

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonNode node, string property) =>
        node[property]?.GetValue<string>() is { Length: > 0 } value ? value : throw new SecretStoreException($"The key service answered without '{property}'.");

    private static KeyVersions Versions(JsonNode data)
    {
        var latest = data["latest_version"]?.GetValue<int>() ?? 1;
        var minimum = Math.Max(1, data["min_decryption_version"]?.GetValue<int>() ?? 1);
        Dictionary<int, string>? publicKeys = null;
        if (data["keys"] is JsonObject keys)
        {
            foreach (var (version, value) in keys)
            {
                if (value is JsonObject entry && entry["public_key"]?.GetValue<string>() is { Length: > 0 } pem
                    && int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= minimum)
                {
                    publicKeys ??= [];
                    publicKeys[number] = pem;
                }
            }
        }

        return new KeyVersions(latest, minimum, publicKeys);
    }
}
