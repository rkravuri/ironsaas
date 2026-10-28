using System.Net;
using System.Text.Json;

namespace Iron.Smoke;

/// <summary>
/// The smoke's step for S3 T10b (ADR-0056): a person of the run with no role is refused 100 times; within the minutes
/// the rules take, the tenant holds a security alert for them, its link to the audit search finds the denials, the
/// control plane holds its copy, and the operator's alerting has it. The alert is resolved and the person deactivated at
/// the end, whatever the result, so runs leave nobody behind.
/// </summary>
public sealed class MonitoringSteps(Api api, SmokeOptions options, SignIn signIn, SessionSteps people, Func<CancellationToken, Task<string>> signInAdministrator, string? operatorToken, string run)
{
    public const int Denials = 100;

    /// <summary>A pass every minute, its window settling a few seconds: the alert is there within two passes.</summary>
    private static readonly TimeSpan AlertWithin = TimeSpan.FromSeconds(150);

    public async Task<string> DenialsRaiseAnAlertAsync(CancellationToken cancellationToken)
    {
        string? personId = null;
        string? admin = null;
        var finished = false;
        try
        {
            admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
            var (id, loginId, password) = await people.PersonAsync(admin, "smoke-denials", cancellationToken).ConfigureAwait(false);
            personId = id;
            var session = await signIn.AsAsync(loginId, password, null, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < Denials; i++)
            {
                (await TenantAsync(HttpMethod.Get, "/api/v1/audit-events?page_size=1", session, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.Forbidden, "a request without the permission");
            }

            var alert = await WaitAsync(
                async () =>
                {
                    var page = (await TenantAsync(HttpMethod.Get, $"/api/v1/alerts?filter=rule:eq:permission_denials&filter=group_value:eq:{id}", admin, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the tenant's security alerts");
                    return page.Body!.Value.GetProperty("data").EnumerateArray().Select(a => (JsonElement?)a.Clone()).FirstOrDefault();
                },
                AlertWithin,
                $"no security alert for the {Denials} denials of the run's person within {AlertWithin.TotalSeconds} s: is the unit's worker running its rules?",
                cancellationToken).ConfigureAwait(false);
            var alertId = alert.GetProperty("id").GetString()!;
            var count = alert.GetProperty("count").GetInt64();
            if (count < Denials)
            {
                throw new SmokeException($"the alert counted {count} denials, not {Denials}");
            }

            // The alert's link finds its events: the same search on the API, every one a denial of the person.
            var search = alert.GetProperty("search").GetString() ?? throw new SmokeException("the alert has no search");
            var found = (await TenantAsync(HttpMethod.Get, search, admin, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the alert's audit search");
            var events = found.Body!.Value.GetProperty("data").EnumerateArray().ToList();
            if (events.Count < Denials || events.Any(e => e.GetProperty("event_type").GetString() != "authz.permission_denied" || e.GetProperty("actor_id").GetString() != id))
            {
                throw new SmokeException($"the alert's search found {events.Count} events, not the person's {Denials} denials");
            }

            var where = new List<string> { $"alert {alertId} with {count} denials, its search finding them" };
            if (operatorToken is not null)
            {
                await WaitAsync(
                    async () =>
                    {
                        var copies = (await api.SendAsync(HttpMethod.Get, options.ControlUrl, $"/api/v1/alerts?filter=tenant:eq:{options.Tenant}&filter=source_id:eq:{alertId}", bearer: operatorToken, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the control plane's alerts");
                        return copies.Body!.Value.GetProperty("data").EnumerateArray().Select(a => (JsonElement?)a.Clone()).FirstOrDefault();
                    },
                    TimeSpan.FromSeconds(60),
                    "the control plane holds no copy of the alert within 60 s: does the unit reach it as its client?",
                    cancellationToken).ConfigureAwait(false);
                where.Add("copied to the control plane");
            }

            if (options.AlertmanagerUrl is not null)
            {
                await WaitAsync(
                    async () =>
                    {
                        var answer = await api.SendAsync(HttpMethod.Get, options.AlertmanagerUrl, $"/api/v2/alerts?filter={Uri.EscapeDataString($"alert_id=\"{alertId}\"")}", cancellationToken: cancellationToken).ConfigureAwait(false);
                        return answer.Status == HttpStatusCode.OK && answer.Body is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0 ? (JsonElement?)list.Clone() : null;
                    },
                    TimeSpan.FromSeconds(90),
                    "the operator's alerting has not received the alert within 90 s: does the control plane reach Alertmanager?",
                    cancellationToken).ConfigureAwait(false);
                where.Add("received by Alertmanager");
            }

            (await TenantAsync(HttpMethod.Post, $"/api/v1/alerts/{alertId}/resolve", admin, cancellationToken, new { note = $"Smoke run {run}: the denials were the smoke's own." }).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "resolve the alert");
            finished = true;
            return string.Join(", ", where) + ", then resolved";
        }
        finally
        {
            await people.DeactivateAsync(admin, personId, finished, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<JsonElement> WaitAsync(Func<Task<JsonElement?>> probe, TimeSpan limit, string failure, CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + limit;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (await probe().ConfigureAwait(false) is { } found)
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }

        throw new SmokeException(failure);
    }

    private Task<Answer> TenantAsync(HttpMethod method, string path, string cookie, CancellationToken cancellationToken, object? json = null) =>
        api.SendAsync(method, options.IngressUrl, path, host: options.Hostname, cookie: cookie, json: json, cancellationToken: cancellationToken);
}
