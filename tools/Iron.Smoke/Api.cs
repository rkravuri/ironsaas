using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Iron.Smoke;

/// <summary>
/// What one call returned: the status, the body as JSON when it is, the correlation id from the header or the problem,
/// the cookie set, and where a redirect points (a company login's answer, ADR-0048).
/// </summary>
public sealed record Answer(HttpStatusCode Status, JsonElement? Body, string? CorrelationId, string? SetCookie, Uri? Location = null)
{
    public bool Ok => (int)Status is >= 200 and < 300;

    public string? Text(string property) => Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public Answer Expect(HttpStatusCode expected, string what)
    {
        if (Status != expected)
        {
            var detail = Text("detail") ?? Text("title") ?? Body?.ToString() ?? string.Empty;
            throw new SmokeException($"{what}: expected {(int)expected} but got {(int)Status}. {Truncate(detail)} correlation {CorrelationId ?? "none"}");
        }

        return this;
    }

    private static string Truncate(string text) => text.Length > 300 ? text[..300] + "..." : text;
}

/// <summary>
/// HTTP as the smoke needs it: a Host header for the tenant hostname through the ingress, bearer tokens, the session
/// cookie carried by hand (so http on the laptop works despite the Secure flag), and the correlation id of every answer.
/// </summary>
public sealed class Api : IDisposable
{
    private readonly HttpClient _http;

    public Api()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public void Dispose() => _http.Dispose();

    public async Task<Answer> SendAsync(HttpMethod method, Uri baseUrl, string path, string? host = null, string? bearer = null, string? cookie = null, object? json = null, (string ClientId, string Secret)? basic = null, string? form = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, new Uri(baseUrl, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (basic is { } credentials)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.ClientId}:{credentials.Secret}")));
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }
        else if (form is not null)
        {
            request.Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonElement? body = null;
        if (text.Length > 0 && (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                body = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                body = null;
            }
        }

        var correlation = response.Headers.TryGetValues("X-Correlation-Id", out var values) ? values.FirstOrDefault() : null;
        if (body is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("correlation_id", out var fromBody) && fromBody.ValueKind == JsonValueKind.String)
        {
            correlation ??= fromBody.GetString();
        }

        string? setCookie = null;
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            // The cookie set, not one cleared: a sign-in's last step clears the sign-in cookie as it sets the session (ADR-0047).
            setCookie = cookies.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.IndexOf('=', StringComparison.Ordinal) is var at && at > 0 && at < c.Length - 1);
        }

        return new Answer(response.StatusCode, body, correlation, setCookie, response.Headers.Location);
    }
}
