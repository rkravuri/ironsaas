using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iron.Kernel.Api.ControlPlane;

/// <summary>
/// The unit's own client in the control plane (<c>ControlPlane</c>, ADR-0050): its address as the units see it, and the
/// client id and secret the fleet made for this unit. Without them, as on a host run from source, nothing is anchored and
/// no key rotation is read (ADR-0054).
/// </summary>
public sealed class ControlPlaneOptions
{
    public const string SectionName = "ControlPlane";

    public string? Address { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>
/// Talks to the control plane as the unit's client (ADR-0050): a client credentials token, kept until a minute before it
/// ends, on every call. The control plane binds the client to this unit's tenant, so a call can only ever be this unit's.
/// The Audit module sends its anchors through it, the Keys module reads its rotations and reports its re-encryption
/// (ADR-0054); one per process, registered by whichever needs it first.
/// </summary>
public sealed class ControlPlaneClient(ControlPlaneOptions options, TimeProvider time, HttpMessageHandler handler) : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly HttpClient _http = new(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };

    public ControlPlaneClient(ControlPlaneOptions options, TimeProvider time)
        : this(options, time, new HttpClientHandler())
    {
    }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenEnds;

    public bool IsConfigured => options.IsConfigured;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, new Uri(new Uri(options.Address!), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _token = null; // a restarted control plane or a new signing key: the next call asks again
        }

        return response;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && time.GetUtcNow() < _tokenEnds)
            {
                return _token;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.Address!), "/connect/token"))
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")]),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ClientId}:{options.ClientSecret}")));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var answer = await response.Content.ReadFromJsonAsync<TokenAnswer>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The control plane answered no token.");
            _token = answer.AccessToken;
            _tokenEnds = time.GetUtcNow().AddSeconds(Math.Max(30, answer.ExpiresIn - 60));
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record TokenAnswer([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
