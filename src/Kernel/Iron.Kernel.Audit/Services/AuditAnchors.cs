using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Audit.Services;

/// <summary>
/// The unit's own client in the control plane (<c>ControlPlane</c>, ADR-0050): its address as the units see it, and the
/// client id and secret the fleet made for this unit. Without them, as on a host run from source, nothing is anchored.
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
/// </summary>
public sealed class ControlPlaneClient(ControlPlaneOptions options, TimeProvider time) : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
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

/// <summary>How far this process knows the control plane has the unit's anchors; asked again after a restart.</summary>
public sealed class AnchorProgress
{
    public long? SealedUpTo { get; set; }

    public long? PrunedUpTo { get; set; }
}

/// <summary>
/// Sends the anchors the control plane does not have yet (TF-061, ADR-0050): one per seal, the position and hash the chain
/// reached, and one per pruning, how far the oldest end went. The same anchor sent again is answered as taken; another
/// hash for an anchored position is refused there and raises the alert, so the unit moves on and the verification
/// reports it. A control plane it cannot reach is tried again at the next pass.
/// </summary>
public sealed class AuditAnchorSender(AuditDbContext context, ControlPlaneClient client, AnchorProgress progress, ILogger<AuditAnchorSender> logger)
{
    public async Task<int> SendAsync(CancellationToken cancellationToken)
    {
        if (!client.IsConfigured)
        {
            return 0;
        }

        try
        {
            if (progress.SealedUpTo is null || progress.PrunedUpTo is null)
            {
                using var latest = await client.SendAsync(HttpMethod.Get, "/api/v1/audit-anchors/latest", null, cancellationToken).ConfigureAwait(false);
                latest.EnsureSuccessStatusCode();
                var answer = await latest.Content.ReadFromJsonAsync<LatestAnchors>(ControlPlaneClient.Json, cancellationToken).ConfigureAwait(false);
                progress.SealedUpTo = answer?.Seal?.Position ?? 0;
                progress.PrunedUpTo = answer?.Pruned?.Position ?? 0;
            }

            var sent = 0;
            var seals = await context.Seals.AsNoTracking()
                .Where(s => s.LastPosition > progress.SealedUpTo)
                .OrderBy(s => s.LastPosition)
                .Take(100)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var seal in seals)
            {
                if (!await SendOneAsync(new AuditAnchor(AuditAnchorKinds.Seal, seal.LastPosition, seal.LastHash, seal.CreatedAt), cancellationToken).ConfigureAwait(false))
                {
                    return sent;
                }

                progress.SealedUpTo = seal.LastPosition;
                sent++;
            }

            var prunes = await context.Prunes.AsNoTracking()
                .Where(p => p.UpToPosition > progress.PrunedUpTo)
                .OrderBy(p => p.UpToPosition)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var prune in prunes)
            {
                if (!await SendOneAsync(new AuditAnchor(AuditAnchorKinds.Pruned, prune.UpToPosition, prune.HashAtPosition, prune.CreatedAt), cancellationToken).ConfigureAwait(false))
                {
                    return sent;
                }

                progress.PrunedUpTo = prune.UpToPosition;
                sent++;
            }

            return sent;
        }
        catch (HttpRequestException exception)
        {
            AuditMetrics.AnchorFailures.Add(1);
            logger.LogWarning("The control plane could not take the audit anchors; they are sent at the next pass: {Message}", exception.Message);
            return 0;
        }
    }

    private async Task<bool> SendOneAsync(AuditAnchor anchor, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(HttpMethod.Post, "/api/v1/audit-anchors", new AnchorBody(anchor.Kind, anchor.Position, anchor.Hash, anchor.At), cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            logger.LogError("ALERT: the control plane holds another hash for audit position {Position} ({Kind}); the anchor there stands and the verification reports it.", anchor.Position, anchor.Kind);
            return true;
        }

        AuditMetrics.AnchorFailures.Add(1);
        logger.LogWarning("The control plane answered {Status} to the audit anchor at position {Position}; it is sent at the next pass.", (int)response.StatusCode, anchor.Position);
        return false;
    }

    private sealed record AnchorBody(string Kind, long Position, string Hash, DateTimeOffset At);

    private sealed record LatestAnchor(long Position, string Hash, DateTimeOffset At);

    private sealed record LatestAnchors(LatestAnchor? Seal, LatestAnchor? Pruned);
}
