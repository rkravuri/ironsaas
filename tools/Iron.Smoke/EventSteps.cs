using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Iron.Smoke;

/// <summary>
/// The smoke's step for S4 T1 (TF-085, ADR-0060): a sample record created through the ingress raises
/// <c>samples.record.created</c> in its own transaction; the unit's worker hands it to the sample module's subscriber, which
/// marks the record noticed. The step polls the events API until the subscriber handled the event, checks there is one
/// event and the record is noticed, and reports how long the handing on took by the unit's own clock, from the event's
/// time to its handling. The record stays, as the visibility step's do.
/// </summary>
public sealed class EventSteps(Api api, SmokeOptions options, Func<CancellationToken, Task<string>> signInAdministrator, string run)
{
    public const string EventType = "samples.record.created";
    public const string Subscriber = "samples.notice_created";

    /// <summary>TF-085 AC1's bound: an event is dispatched within 10 seconds of its change.</summary>
    public static readonly TimeSpan Within = TimeSpan.FromSeconds(10);

    public async Task<string> SampleEventHandledAsync(CancellationToken cancellationToken)
    {
        var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
        var probe = await TenantAsync(HttpMethod.Get, "/api/v1/sample-records?page_size=1", admin, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (probe.Status == HttpStatusCode.NotFound)
        {
            throw new SmokeException($"{options.Tenant}'s unit does not run the sample module: make sample-records TENANT={options.Tenant}, then make update-unit TENANT={options.Tenant} (ADR-0057)");
        }

        var id = (await TenantAsync(HttpMethod.Post, "/api/v1/sample-records", admin, new { name = $"Smoke events {run}" }, cancellationToken).ConfigureAwait(false))
            .Expect(HttpStatusCode.Created, "the run's sample record").Text("id") ?? throw new SmokeException("the new sample record has no id");
        var filter = Uri.EscapeDataString($"record_id:eq:{id}");
        var waited = Stopwatch.StartNew();
        JsonElement found = default;
        JsonElement delivery = default;
        while (waited.Elapsed < Within + TimeSpan.FromSeconds(5))
        {
            var answer = (await TenantAsync(HttpMethod.Get, $"/api/v1/events?filter={filter}", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the record's events");
            var events = answer.Body!.Value.GetProperty("data").EnumerateArray().Where(e => e.GetProperty("type").GetString() == EventType).ToList();
            if (events.Count > 1)
            {
                throw new SmokeException($"the record {id} raised {events.Count} {EventType} events; one change raises one");
            }

            if (events.Count == 1)
            {
                found = events[0];
                delivery = found.GetProperty("deliveries").EnumerateArray().FirstOrDefault(d => d.GetProperty("subscriber").GetString() == Subscriber);
                var status = delivery.ValueKind == JsonValueKind.Object ? delivery.GetProperty("status").GetString() : null;
                if (status == "handled")
                {
                    break;
                }

                if (status == "dead")
                {
                    throw new SmokeException($"{Subscriber} parked the record's event as a dead letter: {delivery.GetProperty("last_error").GetString()}");
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        if (delivery.ValueKind != JsonValueKind.Object || delivery.GetProperty("status").GetString() != "handled")
        {
            throw new SmokeException($"{Subscriber} did not handle the record's event within {waited.Elapsed.TotalSeconds:0} s ({(found.ValueKind == JsonValueKind.Object ? $"the event is {found.GetProperty("status").GetString()}" : "no event listed")}); is the unit's worker running?");
        }

        var occurred = DateTimeOffset.Parse(found.GetProperty("occurred_at").GetString()!, CultureInfo.InvariantCulture);
        var handled = DateTimeOffset.Parse(delivery.GetProperty("handled_at").GetString()!, CultureInfo.InvariantCulture);
        var latency = handled - occurred;
        if (latency > Within)
        {
            throw new SmokeException($"{Subscriber} handled the record's event {latency.TotalSeconds:0.0} s after it occurred; TF-085 AC1 asks for {Within.TotalSeconds:0} s at most");
        }

        var record = (await TenantAsync(HttpMethod.Get, $"/api/v1/sample-records/{id}", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the run's sample record");
        var noticed = record.Text("noticed_at") ?? throw new SmokeException($"{Subscriber} handled the event but the record {id} is not noticed");
        return string.Create(CultureInfo.InvariantCulture, $"sample record {id} raised {EventType} v1 at position {found.GetProperty("position").GetInt64()} in its transaction; {Subscriber} handled it {latency.TotalMilliseconds:0} ms after it occurred (the smoke saw it after {waited.Elapsed.TotalSeconds:0.0} s) and noticed the record at {noticed}");
    }

    private Task<Answer> TenantAsync(HttpMethod method, string path, string cookie, object? json = null, CancellationToken cancellationToken = default) =>
        api.SendAsync(method, options.IngressUrl, path, host: options.Hostname, cookie: cookie, json: json, cancellationToken: cancellationToken);
}
