using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Iron.Kernel.Identity.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Iron.Kernel.Identity.Services;

/// <summary>What a provider's discovery document says this product needs (OpenID Connect Discovery 1.0).</summary>
public sealed record OidcMetadata(
    string Issuer,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri JwksUri,
    Uri? EndSessionEndpoint,
    IReadOnlyList<string> ResponseTypes,
    IReadOnlyList<string> CodeChallengeMethods,
    IReadOnlyList<string> SigningAlgorithms,
    IReadOnlyList<string> TokenEndpointAuthMethods)
{
    /// <summary>Client secret in the form's body unless the provider says it takes only Basic authentication.</summary>
    public bool PostsClientSecret => TokenEndpointAuthMethods.Count == 0 || TokenEndpointAuthMethods.Contains("client_secret_post", StringComparer.Ordinal);
}

/// <summary>
/// Reads a provider's discovery document and its signing keys (ADR-0048), and keeps both for an hour. The keys are read
/// again when a token names a key the set does not hold, a provider having rotated its keys, but at most every thirty
/// seconds, so a stream of tokens with unknown keys cannot make the unit hammer the provider. HTTPS is required, except
/// for a loopback name (<c>localhost</c>, <c>*.localhost</c>, <c>127.0.0.1</c>), which only a test provider can have.
/// </summary>
public sealed class OidcDiscovery(IHttpClientFactory factory, TimeProvider time, ILogger<OidcDiscovery> logger)
{
    public const string HttpClientName = "sso";

    public const string WellKnownPath = "/.well-known/openid-configuration";

    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(1);

    public static readonly TimeSpan RefreshNoSoonerThan = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (OidcMetadata Metadata, DateTimeOffset ReadAt)> _metadata = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, (JsonWebKeySet Keys, DateTimeOffset ReadAt)> _keys = new(StringComparer.Ordinal);

    /// <summary>An address a provider may have: HTTPS, or HTTP on a loopback name; anything else is the error, naming the field.</summary>
    public static Error? CheckAddress(string? address, string field)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return Error.Validation(IdentityErrors.SsoInvalid, "The address is not a web address.", new FieldError(field, "invalid", "Type the full address, starting with https://."));
        }

        if (uri.Scheme == "http" && !IsLoopback(uri))
        {
            return Error.Validation(IdentityErrors.SsoInvalid, "A provider is reached over HTTPS only.", new FieldError(field, "not_https", "Use the provider's https:// address."));
        }

        return null;
    }

    public static bool IsLoopback(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The discovery document at <paramref name="discoveryUrl"/>, from the hour's copy unless <paramref name="fresh"/>.</summary>
    public async Task<Result<OidcMetadata>> MetadataAsync(string discoveryUrl, CancellationToken cancellationToken, bool fresh = false)
    {
        if (CheckAddress(discoveryUrl, "discovery_url") is { } invalid)
        {
            return invalid;
        }

        var now = time.GetUtcNow();
        if (!fresh && _metadata.TryGetValue(discoveryUrl, out var known) && now - known.ReadAt < KeepFor)
        {
            return known.Metadata;
        }

        var read = await GetJsonAsync(new Uri(discoveryUrl), "the discovery document", cancellationToken).ConfigureAwait(false);
        if (read.IsFailure)
        {
            return read.Error;
        }

        var parsed = Parse(discoveryUrl, read.Value);
        if (parsed.IsSuccess)
        {
            _metadata[discoveryUrl] = (parsed.Value, now);
        }

        return parsed;
    }

    /// <summary>
    /// The provider's signing keys, from the hour's copy unless <paramref name="refresh"/> asks for a new read, which is
    /// granted when the copy is older than thirty seconds.
    /// </summary>
    public async Task<Result<JsonWebKeySet>> KeysAsync(OidcMetadata metadata, bool refresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var key = metadata.JwksUri.AbsoluteUri;
        var now = time.GetUtcNow();
        if (_keys.TryGetValue(key, out var known) && now - known.ReadAt < (refresh ? RefreshNoSoonerThan : KeepFor))
        {
            return known.Keys;
        }

        var read = await GetJsonAsync(metadata.JwksUri, "the signing keys", cancellationToken).ConfigureAwait(false);
        if (read.IsFailure)
        {
            return read.Error;
        }

        try
        {
            var keys = new JsonWebKeySet(read.Value.GetRawText());
            _keys[key] = (keys, now);
            if (refresh)
            {
                logger.LogInformation("Read the signing keys of {Issuer} again: {Count} key(s).", metadata.Issuer, keys.Keys.Count);
            }

            return keys;
        }
        catch (ArgumentException exception)
        {
            return Error.Unavailable(IdentityErrors.SsoUnavailable, $"The provider's signing keys could not be read: {exception.Message}");
        }
    }

    private async Task<Result<JsonElement>> GetJsonAsync(Uri address, string what, CancellationToken cancellationToken)
    {
        try
        {
            using var http = factory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(address, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Error.Unavailable(IdentityErrors.SsoUnavailable, $"The provider answered {(int)response.StatusCode} for {what} at {address}.");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Reading {What} at {Address} failed: {Message}", what, address, exception.Message);
            return Error.Unavailable(IdentityErrors.SsoUnavailable, $"The provider could not be reached for {what} at {address}: {exception.Message}");
        }
    }

    /// <summary>
    /// The document's fields, checked: the issuer must be the discovery address without its well-known path (Discovery
    /// section 4.3), so a document cannot speak for another issuer, and every endpoint must be an address a provider may have.
    /// </summary>
    private static Result<OidcMetadata> Parse(string discoveryUrl, JsonElement document)
    {
        string? Text(string name) => document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        IReadOnlyList<string> List(string name) => document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
            : [];

        if (document.ValueKind != JsonValueKind.Object)
        {
            return Error.Unavailable(IdentityErrors.SsoUnavailable, "The discovery document is not a JSON object.");
        }

        var issuer = Text("issuer");
        var expected = discoveryUrl.EndsWith(WellKnownPath, StringComparison.Ordinal) ? discoveryUrl[..^WellKnownPath.Length] : null;
        if (issuer is null || expected is null || !string.Equals(issuer.TrimEnd('/'), expected.TrimEnd('/'), StringComparison.Ordinal))
        {
            return Error.Unavailable(IdentityErrors.SsoUnavailable, $"The discovery document names the issuer '{issuer}', which is not the address it was read from ('{expected ?? discoveryUrl}').");
        }

        var endpoints = new Dictionary<string, Uri?>(StringComparer.Ordinal);
        foreach (var name in new[] { "authorization_endpoint", "token_endpoint", "jwks_uri", "end_session_endpoint" })
        {
            var text = Text(name);
            if (text is null)
            {
                endpoints[name] = null;
                continue;
            }

            if (CheckAddress(text, name) is not null)
            {
                return Error.Unavailable(IdentityErrors.SsoUnavailable, $"The discovery document's {name} ('{text}') is not an address a provider may have.");
            }

            endpoints[name] = new Uri(text);
        }

        foreach (var required in new[] { "authorization_endpoint", "token_endpoint", "jwks_uri" })
        {
            if (endpoints[required] is null)
            {
                return Error.Unavailable(IdentityErrors.SsoUnavailable, $"The discovery document has no {required}.");
            }
        }

        return new OidcMetadata(
            issuer,
            endpoints["authorization_endpoint"]!,
            endpoints["token_endpoint"]!,
            endpoints["jwks_uri"]!,
            endpoints["end_session_endpoint"],
            List("response_types_supported"),
            List("code_challenge_methods_supported"),
            List("id_token_signing_alg_values_supported"),
            List("token_endpoint_auth_methods_supported"));
    }
}
