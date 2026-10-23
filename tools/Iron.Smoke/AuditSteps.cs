using System.Net;
using System.Text.Json;

namespace Iron.Smoke;

/// <summary>
/// The smoke's step for S3 T6 (ADR-0050): the administrator searches the audit trail for this run's own events, by the
/// reason the session step gave, then by the record they name, which answers every event of that person in time order;
/// waits for the sealer to give each its place in the chain, which the chain's head reaches; and exports the same search
/// to CSV, whose file names the filters and holds every event. It changes nothing, so it leaves nothing behind.
/// </summary>
public sealed class AuditSteps(Api api, SmokeOptions options, Func<CancellationToken, Task<string>> signInAdministrator, string run)
{
    private static readonly TimeSpan SealWait = TimeSpan.FromSeconds(60);

    public async Task<string> SearchTheTrailAsync(CancellationToken cancellationToken)
    {
        var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
        var mark = $"Smoke run {run}:";

        // The run's own events, by the reasons its steps gave: the session step's end of a session names its person.
        var ended = (await TenantAsync(HttpMethod.Get, "/api/v1/audit-events?filter=event_type:eq:identity.session_ended&sort=-created_at&page_size=50", admin, cancellationToken).ConfigureAwait(false))
            .Expect(HttpStatusCode.OK, "the trail searched by type");
        var ours = ended.Body!.Value.GetProperty("data").EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("reason").GetString()?.StartsWith(mark, StringComparison.Ordinal) == true);
        if (ours.ValueKind != JsonValueKind.Object)
        {
            throw new SmokeException($"no identity.session_ended event of this run (reason starting \"{mark}\") in the trail");
        }

        var person = ours.GetProperty("record_id").GetString();
        var query = $"filter=record_id:eq:{person}&sort=created_at";
        IReadOnlyList<JsonElement> events = [];
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var found = (await TenantAsync(HttpMethod.Get, $"/api/v1/audit-events?{query}&page_size=200", admin, cancellationToken).ConfigureAwait(false))
                .Expect(HttpStatusCode.OK, "the trail searched by record");
            events = [.. found.Body!.Value.GetProperty("data").EnumerateArray()];
            if (events.All(e => e.TryGetProperty("chain_position", out var position) && position.ValueKind == JsonValueKind.Number) || waited.Elapsed > SealWait)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        var types = events.Select(e => e.GetProperty("event_type").GetString()).ToList();
        foreach (var expected in new[] { "identity.user_created", "identity.session_ended", "identity.user_deactivated" })
        {
            if (!types.Contains(expected))
            {
                throw new SmokeException($"the person's events lack {expected}: {string.Join(", ", types)}");
            }
        }

        var times = events.Select(e => e.GetProperty("created_at").GetDateTimeOffset()).ToList();
        if (!times.Zip(times.Skip(1)).All(pair => pair.First <= pair.Second))
        {
            throw new SmokeException("the person's events are not in time order");
        }

        var unsealed = events.Count(e => e.GetProperty("chain_position").ValueKind != JsonValueKind.Number);
        if (unsealed > 0)
        {
            throw new SmokeException($"{unsealed} of the person's {events.Count} events were not sealed within {SealWait.TotalSeconds:0} s; is the worker running?");
        }

        var last = events.Max(e => e.GetProperty("chain_position").GetInt64());
        var chain = (await TenantAsync(HttpMethod.Get, "/api/v1/audit-chain", admin, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the chain's status");
        var head = chain.Body!.Value.GetProperty("head_position");
        if (head.ValueKind != JsonValueKind.Number || head.GetInt64() < last)
        {
            throw new SmokeException($"the chain's head is {head} but the person's last event is at {last}");
        }

        var export = (await TenantAsync(HttpMethod.Post, $"/api/v1/audit-events/exports?{query}", admin, cancellationToken).ConfigureAwait(false))
            .Expect(HttpStatusCode.Created, "the export of the search");
        var rows = export.Body!.Value.GetProperty("rows").GetInt32();
        var download = export.Text("download_url") ?? throw new SmokeException("the export has no download address");
        var file = (await TenantAsync(HttpMethod.Get, download, admin, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the export's file");
        var csv = file.Content ?? string.Empty;
        if (!csv.StartsWith("# Audit trail export", StringComparison.Ordinal) || !csv.Contains($"record_id:eq:{person}", StringComparison.Ordinal))
        {
            throw new SmokeException("the export's file does not open with its title and filters");
        }

        var missing = events.Count(e => !csv.Contains(e.GetProperty("id").GetString()!, StringComparison.Ordinal));
        if (rows != events.Count || missing > 0)
        {
            throw new SmokeException($"the export holds {rows} rows for {events.Count} events, {missing} of them missing from the file");
        }

        return $"the run's person {person} has {events.Count} events in time order ({string.Join(", ", types)}), sealed up to position {last} under the chain's head {head.GetInt64()}, and exported to CSV with its filters";
    }

    private Task<Answer> TenantAsync(HttpMethod method, string path, string cookie, CancellationToken cancellationToken) =>
        api.SendAsync(method, options.IngressUrl, path, host: options.Hostname, cookie: cookie, cancellationToken: cancellationToken);
}
