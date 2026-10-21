using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Iron.Kernel.Security.Secrets;
using Iron.Smoke;

// The smoke test: the stack as an operator and a user meet it. Each step names the requirement criteria or the decisions it
// proves (ADR-0044), in its report line and in the report make trace reads. Every step is a real call through the ingress
// or the control plane; nothing is mocked. Exit code 0 when every step passes. Eleven steps since S1, three since S2, one
// since S3 T2: the operator's recovery of the administrator, which also sets the password a run did not know.
var options = SmokeOptions.FromEnvironment();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
var ct = cancellation.Token;

Console.WriteLine($"Smoke against {options.ControlUrl} and {options.IngressUrl} as {options.Hostname}");
var report = new SmokeReport(options.Tenant, options.ReportPath);
using var api = new Api();
using var store = new OpenBaoSecretStore(new SecretStoreOptions { Address = options.StoreAddress.ToString(), Token = options.StoreToken, Path = "smoke" });
var host = options.Hostname;
var fleetSecretPath = $"tenants/{options.Tenant}/fleet";
var smokeSecretPath = $"tenants/{options.Tenant}/smoke";

string? operatorToken = null;
await report.StepAsync("control-token", ["ADR-0015", "ADR-0017"], async () =>
{
    var answer = (await api.SendAsync(HttpMethod.Post, options.ControlUrl, "/connect/token", basic: (options.OperatorClientId, options.OperatorSecret), form: "grant_type=client_credentials", cancellationToken: ct)).Expect(HttpStatusCode.OK, "operator token");
    operatorToken = answer.Text("access_token") ?? throw new SmokeException("The token answer has no access_token.");
    return $"operator token for the control plane ({answer.Text("token_type")})";
});

await report.StepAsync("tenant", ["TF-180 AC2"], async () =>
{
    var existing = await api.SendAsync(HttpMethod.Get, options.ControlUrl, $"/api/v1/tenants/{options.Tenant}", bearer: operatorToken, cancellationToken: ct);
    if (existing.Status == HttpStatusCode.NotFound)
    {
        (await api.SendAsync(HttpMethod.Post, options.ControlUrl, "/api/v1/tenants", bearer: operatorToken, json: new { slug = options.Tenant, display_name = "Smoke", administrator_login_id = options.AdministratorLoginId }, cancellationToken: ct)).Expect(HttpStatusCode.Created, "create the tenant");
        existing = (await api.SendAsync(HttpMethod.Get, options.ControlUrl, $"/api/v1/tenants/{options.Tenant}", bearer: operatorToken, cancellationToken: ct)).Expect(HttpStatusCode.OK, "read the tenant");
    }
    else
    {
        existing.Expect(HttpStatusCode.OK, "read the tenant");
    }

    var status = existing.Text("status");
    if (status == "active")
    {
        return $"tenant {options.Tenant} is active already; reused";
    }

    if (status == "provisioning")
    {
        var requested = await api.SendAsync(HttpMethod.Post, options.ControlUrl, $"/api/v1/tenants/{options.Tenant}/provision", bearer: operatorToken, cancellationToken: ct);
        if (requested.Status != HttpStatusCode.Accepted && requested.Status != HttpStatusCode.Conflict)
        {
            requested.Expect(HttpStatusCode.Accepted, "request provisioning");
        }

        var deadline = TimeProvider.System.GetUtcNow().Add(options.ProvisionTimeout);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var job = (await api.SendAsync(HttpMethod.Get, options.ControlUrl, $"/api/v1/tenants/{options.Tenant}/provisioning", bearer: operatorToken, cancellationToken: ct)).Expect(HttpStatusCode.OK, "read the job");
            var jobStatus = job.Text("status");
            if (jobStatus == "completed")
            {
                return $"tenant {options.Tenant} provisioned by job {job.Text("job_id")}";
            }

            if (jobStatus == "failed")
            {
                throw new SmokeException($"provisioning failed at step {job.Text("current_step")}: {job.Text("error")}");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }

        throw new SmokeException($"provisioning did not finish within {options.ProvisionTimeout.TotalSeconds:0} s");
    }

    throw new SmokeException($"tenant {options.Tenant} is {status}; the smoke needs an active or provisioning tenant");
});

// Ready means the tenant's API answers its readiness through the ingress. Just after provisioning the ingress may not
// route to the API yet, while its container starts, and the tenant's shell then answers every path with its page and
// 200; that is not the API (the release after M3 failed on it). So the step waits, at most 90 seconds, for a 200 with
// the API's JSON status.
await report.StepAsync("health", ["TF-001 AC1"], async () =>
{
    var limit = TimeSpan.FromSeconds(90);
    var started = TimeProvider.System.GetTimestamp();
    while (true)
    {
        var answer = await api.SendAsync(HttpMethod.Get, options.IngressUrl, "/health/ready", host: host, cancellationToken: ct);
        var status = answer.Text("status");
        var waited = TimeProvider.System.GetElapsedTime(started);
        if (answer.Status == HttpStatusCode.OK && status is not null)
        {
            return waited < TimeSpan.FromSeconds(1) ? $"{host} ready: {status}" : $"{host} ready: {status}, after {waited.TotalSeconds:0} s of waiting for the API's answer";
        }

        if (waited > limit)
        {
            var seen = answer.Body is null ? "an answer that is not the API's JSON (the tenant's shell answers when the API is not routed)" : $"status {status ?? "missing"}";
            throw new SmokeException($"{host} not ready through the ingress after {limit.TotalSeconds:0} s: the last answer was {(int)answer.Status}, {seen}");
        }

        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
    }
});

string? smokeToken = null;
await report.StepAsync("smoke-token", ["ADR-0015"], async () =>
{
    var fleet = await store.ReadAsync(fleetSecretPath, ct) ?? throw new SmokeException($"no secret at {fleetSecretPath}; was the tenant provisioned by this fleet?");
    var secret = fleet.GetValueOrDefault("smoke_client_secret") ?? throw new SmokeException("the fleet secret has no smoke_client_secret");
    var answer = (await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/connect/token", host: host, basic: ("smoke-client", secret), form: "grant_type=client_credentials", cancellationToken: ct)).Expect(HttpStatusCode.OK, "smoke client token");
    smokeToken = answer.Text("access_token") ?? throw new SmokeException("The token answer has no access_token.");
    return $"token for smoke-client, expires in {answer.Body!.Value.GetProperty("expires_in").GetInt32()} s";
});

await report.StepAsync("token-call", ["TF-023 AC1"], async () =>
{
    var me = (await api.SendAsync(HttpMethod.Get, options.IngressUrl, "/api/v1/me", host: host, bearer: smokeToken, cancellationToken: ct)).Expect(HttpStatusCode.OK, "/me with the token");
    if (me.Text("login_id") != "smoke-client")
    {
        throw new SmokeException($"/me answered {me.Text("login_id")} instead of smoke-client");
    }

    var users = (await api.SendAsync(HttpMethod.Get, options.IngressUrl, "/api/v1/users?page_size=10&sort=login_id", host: host, bearer: smokeToken, cancellationToken: ct)).Expect(HttpStatusCode.OK, "users with the token");
    var count = users.Body!.Value.GetProperty("meta").GetProperty("total_count").GetInt64();
    return $"smoke-client sees itself and lists {count} user(s) with its read permissions; correlation {me.CorrelationId}";
});

string? administratorPassword = null;
await report.StepAsync("administrator", ["TF-018 AC1"], async () =>
{
    var smoke = await store.ReadAsync(smokeSecretPath, ct);
    administratorPassword = smoke?.GetValueOrDefault("administrator_password");
    if (administratorPassword is not null)
    {
        return "administrator password known from an earlier run";
    }

    // No password known: a new tenant's administrator accepts the invitation; one who is active already (a store that
    // healed, an invitation accepted by hand) gets a new password from the operator's recovery in the next step.
    var fleet = await store.ReadAsync(fleetSecretPath, ct) ?? throw new SmokeException($"no secret at {fleetSecretPath}");
    var link = fleet.GetValueOrDefault("invitation_link");
    if (link is null)
    {
        return "no password and no invitation known: the recovery step sets one";
    }

    var token = link[(link.LastIndexOf('/') + 1)..];
    var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    // The password is kept before the accept: if the answer is lost on the way (a resent request meets a spent
    // invitation), the login step still has what the accept used.
    await store.WriteAsync(smokeSecretPath, new Dictionary<string, string>(StringComparer.Ordinal) { ["administrator_password"] = password }, ct);
    administratorPassword = password;
    var accepted = await api.SendAsync(HttpMethod.Post, options.IngressUrl, $"/auth/invitations/{Uri.EscapeDataString(token)}/accept", host: host, json: new { password, display_name = "Smoke Administrator" }, cancellationToken: ct);
    if (accepted.Status == HttpStatusCode.OK)
    {
        return $"invitation accepted for {options.AdministratorLoginId}; password kept at {smokeSecretPath}";
    }

    return $"accept answered {(int)accepted.Status} ({accepted.Text("error_code") ?? "no code"}): the recovery step sets the password";
});

// The operator's recovery (S3 T2, ADR-0046), on every run: the control plane asks the fleet for a link with a reason, the
// unit issues it and audits the operator and the reason, the link sets a new password once and ends every session, a second
// use is refused, the old password no longer signs in, and both planes' audit trails show the request. The new password
// is kept at the smoke's path, so a run that did not know the password ends knowing it.
var recoveryReason = $"The smoke proves the operator's recovery ({TimeProvider.System.GetUtcNow():yyyy-MM-dd HH:mm} UTC)";
await report.StepAsync("recovery", ["TF-017 AC1", "TF-017 AC2", "ADR-0046"], async () =>
{
    var before = administratorPassword;
    var requested = (await api.SendAsync(HttpMethod.Post, options.ControlUrl, $"/api/v1/tenants/{options.Tenant}/administrator-recovery", bearer: operatorToken, json: new { reason = recoveryReason }, cancellationToken: ct)).Expect(HttpStatusCode.Accepted, "ask for the administrator's recovery link");
    var jobId = requested.Text("job_id");
    var output = await JobOutputAsync(jobId);
    var token = output[(output.LastIndexOf('/') + 1)..];
    var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    (await api.SendAsync(HttpMethod.Post, options.IngressUrl, $"/auth/recovery/{Uri.EscapeDataString(token)}", host: host, json: new { password }, cancellationToken: ct)).Expect(HttpStatusCode.OK, "set the password with the link");
    await store.WriteAsync(smokeSecretPath, new Dictionary<string, string>(StringComparer.Ordinal) { ["administrator_password"] = password }, ct);
    administratorPassword = password;

    var again = (await api.SendAsync(HttpMethod.Post, options.IngressUrl, $"/auth/recovery/{Uri.EscapeDataString(token)}", host: host, json: new { password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) }, cancellationToken: ct)).Expect(HttpStatusCode.NotFound, "the link used twice");
    var old = before is null || before == password
        ? "no earlier password to try"
        : $"the earlier password answers {(int)(await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/login", host: host, json: new { login_id = options.AdministratorLoginId, password = before }, cancellationToken: ct)).Expect(HttpStatusCode.Unauthorized, "the earlier password").Status}";

    // The tenant's own audit trail names the operator and the reason; the control plane's has the request.
    var session = (await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/login", host: host, json: new { login_id = options.AdministratorLoginId, password }, cancellationToken: ct)).Expect(HttpStatusCode.OK, "sign in with the new password").SetCookie;
    var issued = Uri.EscapeDataString("event_type:eq:identity.recovery_link_issued");
    var unitTrail = (await api.SendAsync(HttpMethod.Get, options.IngressUrl, $"/api/v1/audit-events?filter={issued}&page_size=5", host: host, cookie: session, cancellationToken: ct)).Expect(HttpStatusCode.OK, "the tenant's audit trail");
    var unitEvent = unitTrail.Body!.Value.GetProperty("data").EnumerateArray().FirstOrDefault(e => e.GetProperty("reason").GetString() == recoveryReason);
    if (unitEvent.ValueKind != JsonValueKind.Object || !(unitEvent.GetProperty("details").ToString().Contains("operator", StringComparison.Ordinal)))
    {
        throw new SmokeException("the tenant's audit trail has no recovery_link_issued with the run's reason and the operator");
    }

    await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/logout", host: host, cookie: session, cancellationToken: ct);
    var requestedFilter = Uri.EscapeDataString("event_type:eq:tenancy.administrator_recovery_requested");
    var controlTrail = (await api.SendAsync(HttpMethod.Get, options.ControlUrl, $"/api/v1/audit-events?filter={requestedFilter}&page_size=5", bearer: operatorToken, cancellationToken: ct)).Expect(HttpStatusCode.OK, "the control plane's audit trail");
    if (!controlTrail.Body!.Value.GetProperty("data").EnumerateArray().Any(e => e.GetProperty("reason").GetString() == recoveryReason))
    {
        throw new SmokeException("the control plane's audit trail has no administrator_recovery_requested with the run's reason");
    }

    return $"job {jobId} issued a link for {options.AdministratorLoginId}; it set the password once, a second use answers 404 {again.Text("error_code")}; {old}; both audit trails show the reason";
});

string? cookie = null;
string? cookieCorrelation = null;
await report.StepAsync("login", ["TF-011 AC1"], async () =>
{
    var answer = (await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/login", host: host, json: new { login_id = options.AdministratorLoginId, password = administratorPassword }, cancellationToken: ct)).Expect(HttpStatusCode.OK, "login");
    cookie = answer.SetCookie ?? throw new SmokeException("login answered 200 without a session cookie");
    return $"session cookie {cookie[..cookie.IndexOf('=', StringComparison.Ordinal)]} for {options.AdministratorLoginId}";
});

await report.StepAsync("cookie-call", ["ADR-0010", "ADR-0014"], async () =>
{
    // The two accounts by filter, not the first page: every run leaves people on a tenant, and a page of fifty sorted by
    // login stopped reaching smoke-client on the laptop's echo (S3 T1).
    var filter = Uri.EscapeDataString($"login_id:in:smoke-client,{options.AdministratorLoginId}");
    var users = (await api.SendAsync(HttpMethod.Get, options.IngressUrl, $"/api/v1/users?page_size=50&sort=login_id&filter={filter}", host: host, cookie: cookie, cancellationToken: ct)).Expect(HttpStatusCode.OK, "users with the cookie");
    var logins = users.Body!.Value.GetProperty("data").EnumerateArray().Select(u => u.GetProperty("login_id").GetString()).ToList();
    if (!logins.Contains("smoke-client") || !logins.Contains(options.AdministratorLoginId))
    {
        throw new SmokeException($"the user list lacks the expected accounts: {string.Join(", ", logins)}");
    }

    cookieCorrelation = users.CorrelationId ?? throw new SmokeException("the answer carries no X-Correlation-Id");
    return $"{logins.Count} user(s) with the cookie; correlation {cookieCorrelation}";
});

await report.StepAsync("denials", ["TF-003 AC1", "ADR-0012"], async () =>
{
    var anonymous = (await api.SendAsync(HttpMethod.Get, options.IngressUrl, "/api/v1/users", host: host, cancellationToken: ct)).Expect(HttpStatusCode.Unauthorized, "anonymous users call");
    var foreign = (await api.SendAsync(HttpMethod.Get, options.IngressUrl, "/api/v1/me", host: host, bearer: operatorToken, cancellationToken: ct)).Expect(HttpStatusCode.Unauthorized, "a control plane token at the tenant");
    var parts = new List<string> { $"anonymous 401 {anonymous.Text("error_code")}", $"other tenant's token 401 {foreign.Text("error_code")}" };
    if (options.ReaderSecret is not null)
    {
        var reader = (await api.SendAsync(HttpMethod.Post, options.ControlUrl, "/connect/token", basic: (options.ReaderClientId, options.ReaderSecret), form: "grant_type=client_credentials", cancellationToken: ct)).Expect(HttpStatusCode.OK, "reader token");
        var denied = (await api.SendAsync(HttpMethod.Post, options.ControlUrl, "/api/v1/tenants", bearer: reader.Text("access_token"), json: new { slug = "denied", display_name = "Denied", administrator_login_id = "admin@denied.localhost" }, cancellationToken: ct)).Expect(HttpStatusCode.Forbidden, "a reader creating a tenant");
        if (denied.Text("error_code") != "authz.permission_denied")
        {
            throw new SmokeException($"the 403 carries error_code {denied.Text("error_code")}");
        }

        parts.Add($"reader creating a tenant 403 {denied.Text("error_code")} correlation {denied.CorrelationId}");
    }
    else
    {
        parts.Add("no SMOKE_READER_SECRET: the 403 case was not run");
    }

    return string.Join("; ", parts);
});

await report.StepAsync("logout", ["ADR-0014"], async () =>
{
    var answer = await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/logout", host: host, cookie: cookie, cancellationToken: ct);
    if (answer.Status is not (HttpStatusCode.NoContent or HttpStatusCode.OK))
    {
        answer.Expect(HttpStatusCode.NoContent, "logout");
    }

    (await api.SendAsync(HttpMethod.Get, options.IngressUrl, "/api/v1/me", host: host, cookie: cookie, cancellationToken: ct)).Expect(HttpStatusCode.Unauthorized, "/me after logout");
    return "the session ended and the old cookie is refused";
});

await report.StepAsync("trace", ["ADR-0010"], async () =>
{
    if (options.TempoUrl is null)
    {
        return "skipped (SMOKE_TEMPO_URL=skip)";
    }

    var deadline = TimeProvider.System.GetUtcNow().AddSeconds(60);
    Answer? answer = null;
    while (TimeProvider.System.GetUtcNow() < deadline)
    {
        answer = await api.SendAsync(HttpMethod.Get, options.TempoUrl, $"/api/traces/{cookieCorrelation}", cancellationToken: ct);
        if (answer.Status == HttpStatusCode.OK && answer.Body is { } trace)
        {
            var spans = CountSpans(trace);
            if (spans > 0)
            {
                return $"trace {cookieCorrelation} found in Tempo with {spans} span(s)";
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
    }

    throw new SmokeException($"trace {cookieCorrelation} not found in Tempo within 60 s (last answer {(answer is null ? "none" : (int)answer.Status)}); is the telemetry profile up?");
});

// S2's three steps (S2-close): a scoped role, a plan change and a kill switch, each restoring what it changed, and the
// person the scoped role makes deactivated (T2). The run's short id names what they make, apart from earlier runs.
var run = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
var access = new AccessSteps(api, options, operatorToken ?? string.Empty, administratorPassword ?? string.Empty, run);
await report.StepAsync("scoped-role", ["TF-033 AC1", "TF-035 AC1", "ADR-0031"], () => access.ScopedRoleAsync(ct));
await report.StepAsync("plan-change", ["TF-046 AC1", "ADR-0034"], () => access.PlanChangeAsync(ct));
await report.StepAsync("kill-switch", ["ADR-0035"], () => access.KillSwitchAsync(ct));

await report.WriteAsync();
return report.Passed ? 0 : 1;

// The recovery job's output once it completes: the link, whose last segment is the code.
async Task<string> JobOutputAsync(string? jobId)
{
    var deadline = TimeProvider.System.GetUtcNow().AddSeconds(120);
    while (TimeProvider.System.GetUtcNow() < deadline)
    {
        var job = (await api.SendAsync(HttpMethod.Get, options.ControlUrl, $"/api/v1/tenants/{options.Tenant}/provisioning", bearer: operatorToken, cancellationToken: ct)).Expect(HttpStatusCode.OK, "read the recovery job");
        if (job.Text("job_id") == jobId)
        {
            switch (job.Text("status"))
            {
                case "completed":
                    return job.Text("output") is { } output && output.Contains("/recovery/", StringComparison.Ordinal)
                        ? output
                        : throw new SmokeException($"the recovery job completed without a link: {job.Text("output")}");
                case "failed":
                    throw new SmokeException($"the recovery job failed: {job.Text("error")}");
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
    }

    throw new SmokeException($"the recovery job {jobId} did not finish within 120 s; is the fleet worker up?");
}

static int CountSpans(JsonElement root)
{
    // Tempo answers OTLP JSON: batches[].scopeSpans[].spans[]. Older shapes use resourceSpans or instrumentationLibrarySpans.
    var count = 0;
    foreach (var batchesName in new[] { "batches", "resourceSpans" })
    {
        if (!root.TryGetProperty(batchesName, out var batches) || batches.ValueKind != JsonValueKind.Array)
        {
            continue;
        }

        foreach (var batch in batches.EnumerateArray())
        {
            foreach (var scopesName in new[] { "scopeSpans", "instrumentationLibrarySpans" })
            {
                if (batch.TryGetProperty(scopesName, out var scopes) && scopes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var scope in scopes.EnumerateArray())
                    {
                        if (scope.TryGetProperty("spans", out var spans) && spans.ValueKind == JsonValueKind.Array)
                        {
                            count += spans.GetArrayLength();
                        }
                    }
                }
            }
        }
    }

    return count;
}
