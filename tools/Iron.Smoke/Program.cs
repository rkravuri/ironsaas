using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Iron.Kernel.Security.Secrets;
using Iron.Smoke;

// The smoke test: the stack as an operator and a user meet it. Each step names the requirement criteria or the decisions it
// proves (ADR-0044), in its report line and in the report make trace reads. Every step is a real call through the ingress
// or the control plane; nothing is mocked. Exit code 0 when every step passes. Eleven steps since S1, three since S2.
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

await report.StepAsync("health", ["TF-001 AC1"], async () =>
{
    var answer = (await api.SendAsync(HttpMethod.Get, options.IngressUrl, "/health/ready", host: host, cancellationToken: ct)).Expect(HttpStatusCode.OK, "readiness through the ingress");
    return $"{host} ready: {answer.Text("status")}";
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
    administratorPassword = options.AdministratorPassword;
    if (administratorPassword is not null)
    {
        return "administrator password from SMOKE_ADMIN_PASSWORD";
    }

    var smoke = await store.ReadAsync(smokeSecretPath, ct);
    administratorPassword = smoke?.GetValueOrDefault("administrator_password");
    if (administratorPassword is not null)
    {
        return "administrator password known from an earlier run";
    }

    var fleet = await store.ReadAsync(fleetSecretPath, ct) ?? throw new SmokeException($"no secret at {fleetSecretPath}");
    var link = fleet.GetValueOrDefault("invitation_link") ?? throw new SmokeException("the fleet secret has no invitation_link: run an update of the unit (make update-unit), which re-issues it while the administrator is still invited");
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

    return $"accept answered {(int)accepted.Status} ({accepted.Text("error_code") ?? "no code"}); the login step decides";
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

// S2's three steps (S2-close): a scoped role, a plan change and a kill switch, each restoring what it changed. The run's
// short id names what they make, apart from earlier runs.
var run = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
var access = new AccessSteps(api, options, operatorToken ?? string.Empty, administratorPassword ?? string.Empty, run);
await report.StepAsync("scoped-role", ["TF-033 AC1", "TF-035 AC1", "ADR-0031"], () => access.ScopedRoleAsync(ct));
await report.StepAsync("plan-change", ["TF-046 AC1", "ADR-0034"], () => access.PlanChangeAsync(ct));
await report.StepAsync("kill-switch", ["ADR-0035"], () => access.KillSwitchAsync(ct));

await report.WriteAsync();
return report.Passed ? 0 : 1;

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
