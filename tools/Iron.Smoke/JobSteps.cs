using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Iron.Smoke;

/// <summary>
/// The smoke's step for S4 T2 (TF-120, TF-121, ADR-0061): the tenant's administrator starts the sample module's job
/// <c>samples.tally_records</c> through the job console, with a reason, and the step polls the run until the unit's worker
/// finished it, then checks its log and that the job's last run is this one. The report says how long the run waited for
/// the worker and how long it took, by the unit's own clock.
/// </summary>
public sealed class JobSteps(Api api, SmokeOptions options, Func<CancellationToken, Task<string>> signInAdministrator, string run)
{
    public const string Job = "samples.tally_records";

    /// <summary>How long the worker may take to pick the run up and finish it: its tick is half a second.</summary>
    public static readonly TimeSpan Within = TimeSpan.FromSeconds(30);

    public async Task<string> SampleJobRunsAsync(CancellationToken cancellationToken)
    {
        var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
        var job = await TenantAsync(HttpMethod.Get, $"/api/v1/jobs/{Job}", admin, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (job.Status == HttpStatusCode.NotFound)
        {
            throw new SmokeException($"{options.Tenant}'s unit does not declare {Job}: it runs the sample module only when marked, make sample-records TENANT={options.Tenant}, then make update-unit TENANT={options.Tenant} (ADR-0057)");
        }

        job.Expect(HttpStatusCode.OK, $"the job {Job}");
        var id = (await TenantAsync(HttpMethod.Post, $"/api/v1/jobs/{Job}/run", admin, new { reason = $"Smoke run {run}" }, cancellationToken).ConfigureAwait(false))
            .Expect(HttpStatusCode.OK, $"a run of {Job} started now").Text("id") ?? throw new SmokeException("the started run has no id");
        var waited = Stopwatch.StartNew();
        Answer answer;
        string? status;
        do
        {
            answer = (await TenantAsync(HttpMethod.Get, $"/api/v1/jobs/runs/{id}", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the started run");
            status = answer.Text("status");
            if (status is "succeeded" or "dead")
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        while (waited.Elapsed < Within);

        if (status == "dead")
        {
            throw new SmokeException($"the run {id} of {Job} failed as often as it is retried: {answer.Text("error")}");
        }

        if (status != "succeeded")
        {
            throw new SmokeException($"the run {id} of {Job} is {status} after {waited.Elapsed.TotalSeconds:0} s; is the unit's worker running? Its job console shows why (/console/jobs).");
        }

        var log = answer.Text("log") ?? string.Empty;
        if (!log.Contains("sample record(s)", StringComparison.Ordinal))
        {
            throw new SmokeException($"the run {id} of {Job} succeeded without its log line; its log is '{log.Trim()}'");
        }

        var last = (await TenantAsync(HttpMethod.Get, $"/api/v1/jobs/{Job}", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, $"the job {Job}");
        var lastRun = last.Body!.Value.TryGetProperty("last_run", out var lastValue) && lastValue.ValueKind == JsonValueKind.Object ? lastValue.GetProperty("id").GetString() : null;
        if (lastRun != id)
        {
            throw new SmokeException($"the job {Job} names {lastRun ?? "no run"} as its last run, not the smoke's {id}");
        }

        var due = DateTimeOffset.Parse(answer.Text("due_at")!, CultureInfo.InvariantCulture);
        var started = DateTimeOffset.Parse(answer.Text("started_at")!, CultureInfo.InvariantCulture);
        var took = answer.Body!.Value.GetProperty("duration_ms").GetInt32();
        return string.Create(CultureInfo.InvariantCulture, $"run {id} of {Job} started now with a reason, picked up by the unit's worker {(started - due).TotalMilliseconds:0} ms after it was queued, succeeded in {took} ms ({log.Trim()}); the job's last run is it");
    }

    private Task<Answer> TenantAsync(HttpMethod method, string path, string cookie, object? json = null, CancellationToken cancellationToken = default) =>
        api.SendAsync(method, options.IngressUrl, path, host: options.Hostname, cookie: cookie, json: json, cancellationToken: cancellationToken);
}
