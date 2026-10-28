using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Iron.Smoke;

/// <summary>
/// What one call returned: the status, the body as JSON when it is, the correlation id from the header or the problem,
/// the cookie set, where a redirect points (a company login's answer, ADR-0048), and the text of a body that is not
/// JSON, such as an audit export's CSV (ADR-0050).
/// </summary>
public sealed record Answer(HttpStatusCode Status, JsonElement? Body, string? CorrelationId, string? SetCookie, Uri? Location = null, string? Content = null, int? RetryAfterSeconds = null)
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
/// It stays within the rate limits by waiting, never by raising them: a 429 waits its Retry-After and sends again. A
/// sign-in the API asks to prove itself first, after repeated failures, gets a challenge, solves it here, and is sent
/// again with the answer (TF-072, ADR-0053).
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

    private const int Waits = 6;

    public async Task<Answer> SendAsync(HttpMethod method, Uri baseUrl, string path, string? host = null, string? bearer = null, string? cookie = null, object? json = null, (string ClientId, string Secret)? basic = null, string? form = null, CancellationToken cancellationToken = default)
    {
        string? challenge = null;
        for (var attempt = 1; ; attempt++)
        {
            var body = challenge is null || json is null ? json : WithChallenge(json, challenge);
            var answer = await SendOnceAsync(method, baseUrl, path, host, bearer, cookie, body, basic, form, cancellationToken).ConfigureAwait(false);
            if (answer.Status == HttpStatusCode.TooManyRequests && attempt <= Waits)
            {
                var seconds = answer.RetryAfterSeconds ?? 5;
                Console.WriteLine($"  waiting {seconds} s for the {answer.Text("limit") ?? "rate"} limit, as its Retry-After says");
                await Task.Delay(TimeSpan.FromSeconds(seconds + 1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (answer.Status == HttpStatusCode.Forbidden && path == "/auth/login" && json is not null && attempt <= Waits
                && answer.Text("error_code") is "identity.login.challenge_required" or "identity.login.challenge_invalid")
            {
                challenge = await SolveChallengeAsync(baseUrl, host, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return answer;
        }
    }

    /// <summary>The login's challenge, solved as the shell solves it: each number until the salt's hash with it is the challenge.</summary>
    private async Task<string> SolveChallengeAsync(Uri baseUrl, string? host, CancellationToken cancellationToken)
    {
        var answer = (await SendOnceAsync(HttpMethod.Get, baseUrl, "/auth/challenge", host, null, null, null, null, null, cancellationToken).ConfigureAwait(false))
            .Expect(HttpStatusCode.OK, "the login's challenge");
        var challenge = answer.Text("challenge") ?? throw new SmokeException("the login's challenge has no challenge");
        var salt = answer.Text("salt") ?? throw new SmokeException("the login's challenge has no salt");
        var maximum = answer.Body!.Value.GetProperty("max_number").GetInt64();
        for (long number = 0; number <= maximum; number++)
        {
            if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + number.ToString(CultureInfo.InvariantCulture)))) == challenge)
            {
                Console.WriteLine($"  solved the login's challenge (up to {maximum})");
                var payload = new JsonObject
                {
                    ["algorithm"] = answer.Text("algorithm"),
                    ["challenge"] = challenge,
                    ["number"] = number,
                    ["salt"] = salt,
                    ["signature"] = answer.Text("signature"),
                };
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            }
        }

        throw new SmokeException("the login's challenge has no answer up to its maximum");
    }

    private static JsonObject WithChallenge(object json, string challenge)
    {
        var body = JsonSerializer.SerializeToNode(json)?.AsObject() ?? new JsonObject();
        body["challenge"] = challenge;
        return body;
    }

    private async Task<Answer> SendOnceAsync(HttpMethod method, Uri baseUrl, string path, string? host, string? bearer, string? cookie, object? json, (string ClientId, string Secret)? basic, string? form, CancellationToken cancellationToken)
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

        var retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int?)Math.Ceiling(delta.TotalSeconds) : null;
        return new Answer(response.StatusCode, body, correlation, setCookie, response.Headers.Location, body is null && text.Length > 0 ? text : null, retryAfter);
    }
}
