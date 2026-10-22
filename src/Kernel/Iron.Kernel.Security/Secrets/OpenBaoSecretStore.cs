using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iron.Kernel.Security.Secrets;

/// <summary>
/// OpenBao (and Vault) KV version 2 over its HTTP API: two endpoints, one header. No client library is needed for that,
/// and the API has been stable for years. Key Vault gets its own implementation of the same interface.
/// </summary>
public sealed class OpenBaoSecretStore : ISecretStore, ISecretStoreAdmin, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _mount;

    public OpenBaoSecretStore(SecretStoreOptions options)
        : this(options, new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
    {
    }

    public OpenBaoSecretStore(SecretStoreOptions options, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException("The secret store needs Secrets:Address and Secrets:Token.");
        }

        _http = http;
        _http.BaseAddress = new Uri(options.Address!.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Remove("X-Vault-Token");
        _http.DefaultRequestHeaders.Add("X-Vault-Token", options.Token);
        _mount = options.Mount.Trim('/');
    }

    public async Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(DataUri(path), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "read", path, cancellationToken).ConfigureAwait(false);
        var envelope = await response.Content.ReadFromJsonAsync<ReadEnvelope>(cancellationToken).ConfigureAwait(false);
        var values = envelope?.Data?.Data;
        if (values is null)
        {
            return null;
        }

        return values.ToDictionary(pair => pair.Key, pair => pair.Value.ValueKind == JsonValueKind.String ? pair.Value.GetString() ?? string.Empty : pair.Value.ToString(), StringComparer.Ordinal);
    }

    public async Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        using var response = await _http.PostAsJsonAsync(DataUri(path), new WriteEnvelope(values), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "write", path, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteOnceAsync(string path, IReadOnlyDictionary<string, string> values, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "A one-time place needs a lifetime above zero.");
        }

        // The lifetime first, on the path's metadata, so the version written next is deleted by the store when it ends;
        // then the value with check-and-set 0, which the store refuses if anything was ever written there.
        var after = $"{(long)Math.Ceiling(lifetime.TotalSeconds)}s";
        using (var metadata = await _http.PostAsJsonAsync(MetadataUri(path), new MetadataEnvelope(after), cancellationToken).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(metadata, "set the lifetime of", path, cancellationToken).ConfigureAwait(false);
        }

        using var response = await _http.PostAsJsonAsync(DataUri(path), new WriteOnceEnvelope(new WriteOptions(0), values), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "write once to", path, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.DeleteAsync(MetadataUri(path), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, "delete", path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri("v1/sys/health", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            // 200 active, 429 standby: both answer requests. Sealed (503) or uninitialised (501) do not.
            return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.TooManyRequests;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task CreateReadPolicyAsync(string policyName, IReadOnlyList<string> paths, CancellationToken cancellationToken, IReadOnlyList<string>? managedPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentNullException.ThrowIfNull(paths);
        var read = paths.Select(path => $"path \"{_mount}/data/{path.Trim('/')}\" {{ capabilities = [\"read\"] }}\npath \"{_mount}/metadata/{path.Trim('/')}\" {{ capabilities = [\"read\"] }}");
        var managed = (managedPaths ?? []).Select(path => $"path \"{_mount}/data/{path.Trim('/')}\" {{ capabilities = [\"create\", \"read\", \"update\", \"delete\"] }}\npath \"{_mount}/metadata/{path.Trim('/')}\" {{ capabilities = [\"read\", \"delete\"] }}");
        var rules = string.Join("\n", read.Concat(managed));
        using var response = await _http.PutAsJsonAsync(new Uri($"v1/sys/policies/acl/{policyName}", UriKind.Relative), new PolicyEnvelope(rules), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "write the policy for", policyName, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateDepositPolicyAsync(string policyName, string pathPattern, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathPattern);
        var pattern = pathPattern.Trim('/');
        var rules = $"path \"{_mount}/data/{pattern}\" {{ capabilities = [\"create\", \"update\"] }}\npath \"{_mount}/metadata/{pattern}\" {{ capabilities = [\"create\", \"update\"] }}";
        using var response = await _http.PutAsJsonAsync(new Uri($"v1/sys/policies/acl/{policyName}", UriKind.Relative), new PolicyEnvelope(rules), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "write the policy for", policyName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> CreateTokenAsync(IReadOnlyList<string> policies, string displayName, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var request = new TokenRequest(policies, displayName, $"{(long)timeToLive.TotalSeconds}s", true, true);
        using var response = await _http.PostAsJsonAsync(new Uri("v1/auth/token/create", UriKind.Relative), request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "create a token for", displayName, cancellationToken).ConfigureAwait(false);
        var envelope = await response.Content.ReadFromJsonAsync<TokenEnvelope>(cancellationToken).ConfigureAwait(false);
        return envelope?.Auth?.ClientToken ?? throw new SecretStoreException("The store created a token but did not return it.");
    }

    public void Dispose() => _http.Dispose();

    private Uri DataUri(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Uri($"v1/{_mount}/data/{path.Trim('/')}", UriKind.Relative);
    }

    private Uri MetadataUri(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Uri($"v1/{_mount}/metadata/{path.Trim('/')}", UriKind.Relative);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, string path, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new SecretStoreException($"The secret store refused to {operation} '{path}': {(int)response.StatusCode} {response.ReasonPhrase}. {Trim(body)}");
    }

    private static string Trim(string body) => body.Length > 300 ? body[..300] : body;

}

internal sealed record ReadEnvelope([property: JsonPropertyName("data")] ReadData? Data);

internal sealed record ReadData([property: JsonPropertyName("data")] Dictionary<string, JsonElement>? Data);

internal sealed record WriteEnvelope([property: JsonPropertyName("data")] IReadOnlyDictionary<string, string> Data);

internal sealed record WriteOptions([property: JsonPropertyName("cas")] int CheckAndSet);

internal sealed record WriteOnceEnvelope([property: JsonPropertyName("options")] WriteOptions Options, [property: JsonPropertyName("data")] IReadOnlyDictionary<string, string> Data);

internal sealed record MetadataEnvelope([property: JsonPropertyName("delete_version_after")] string DeleteVersionAfter);

internal sealed record PolicyEnvelope([property: JsonPropertyName("policy")] string Policy);

internal sealed record TokenRequest(
    [property: JsonPropertyName("policies")] IReadOnlyList<string> Policies,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("ttl")] string TimeToLive,
    [property: JsonPropertyName("renewable")] bool Renewable,
    [property: JsonPropertyName("no_parent")] bool NoParent);

internal sealed record TokenEnvelope([property: JsonPropertyName("auth")] TokenAuth? Auth);

internal sealed record TokenAuth([property: JsonPropertyName("client_token")] string? ClientToken);

public sealed class SecretStoreException : Exception
{
    public SecretStoreException()
    {
    }

    public SecretStoreException(string message)
        : base(message)
    {
    }

    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
