using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Iron.Smoke;

/// <summary>
/// The smoke's three steps for S2 (S2-close): a scoped role, a plan change and a kill switch, through the API as an
/// administrator and an operator meet them. Each restores what it changed, also when it fails, and says so if it could
/// not; what a step makes carries the run's short id, apart from earlier runs. Each wait is timed and reported.
/// </summary>
public sealed class AccessSteps(Api api, SmokeOptions options, string operatorToken, Func<CancellationToken, Task<string>> signInAdministrator, string run)
{
    private const string RoleTransferRefused = "entitlements.feature_disabled";
    private const string Killed = "flags.killed";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A person homed at a unit of the run's own, with the read-only role at that unit and the units below it, lists only
    /// that unit's people and is answered not found for the tenant's administrator. The person is deactivated at the end,
    /// whatever the result (T2), with the readers earlier runs left active, so runs leave nobody behind.
    /// </summary>
    public async Task<string> ScopedRoleAsync(CancellationToken cancellationToken)
    {
        var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
        string? unitId = null;
        string? assignmentId = null;
        string? reader = null;
        string? personId = null;
        var restore = new Restore();
        var finished = false;
        try
        {
            var administratorId = (await TenantAsync(HttpMethod.Get, "/api/v1/me", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "/me").Text("id");
            var tree = (await TenantAsync(HttpMethod.Get, "/api/v1/organisation-units", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the tree");
            var root = Items(tree).First(unit => unit.GetProperty("parent_id").ValueKind == JsonValueKind.Null).GetProperty("id").GetString();
            var unitName = $"Smoke {run}";
            var unit = (await TenantAsync(HttpMethod.Post, $"/api/v1/organisation-units/{root}/children", admin, new { name = unitName, code = $"smoke-{run}", kind = "unit" }, cancellationToken).ConfigureAwait(false))
                .Expect(HttpStatusCode.Created, "the run's unit");
            unitId = unit.Text("id");

            var loginId = $"smoke-reader-{run}@{options.Hostname}";
            var created = (await TenantAsync(HttpMethod.Post, "/api/v1/users", admin, new { login_id = loginId, display_name = $"Smoke reader {run}", home_unit_id = unitId }, cancellationToken).ConfigureAwait(false))
                .Expect(HttpStatusCode.Created, "invite the reader");
            var link = created.Body!.Value.GetProperty("invitation_link").GetString() ?? throw new SmokeException("the invitation answer has no link");
            personId = created.Body!.Value.GetProperty("user").GetProperty("id").GetString();
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            var token = link[(link.IndexOf('#', StringComparison.Ordinal) + 1)..];
            (await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/invitations/accept", host: options.Hostname, json: new { token, password, display_name = $"Smoke reader {run}" }, cancellationToken: cancellationToken).ConfigureAwait(false))
                .Expect(HttpStatusCode.OK, "accept the invitation");

            // The read-only system role: every read permission, here bounded by the unit and the units below it.
            var roles = (await TenantAsync(HttpMethod.Get, "/api/v1/roles?filter=code:eq:read-only", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the read-only role");
            var roleId = Items(roles).Single().GetProperty("id").GetString();
            assignmentId = (await TenantAsync(HttpMethod.Post, "/api/v1/role-assignments", admin, new { principal_id = personId, role_id = roleId, scope_kind = "unit_with_children", unit_id = unitId }, cancellationToken).ConfigureAwait(false))
                .Expect(HttpStatusCode.Created, "grant the role at the unit").Text("id");

            reader = await SignInAsync(loginId, password, cancellationToken).ConfigureAwait(false);
            var users = (await TenantAsync(HttpMethod.Get, "/api/v1/users?page_size=50", reader, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the reader's user list");
            var listed = Items(users).ToList();
            var outside = listed.Where(user => user.GetProperty("home_unit_id").GetString() != unitId).Select(user => user.GetProperty("login_id").GetString()).ToList();
            if (listed.Count == 0 || outside.Count > 0)
            {
                throw new SmokeException($"the reader of {unitName} lists {listed.Count} user(s), of which {outside.Count} outside it: {string.Join(", ", outside)}");
            }

            var administrator = (await TenantAsync(HttpMethod.Get, $"/api/v1/users/{administratorId}", reader, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.NotFound, "the administrator, read by the reader");
            finished = true;
            return $"a reader of {unitName} and below lists {listed.Count} user(s), all of that unit; the administrator by id answers 404 {administrator.Text("error_code")}";
        }
        finally
        {
            if (reader is not null)
            {
                await restore.Async("sign the reader out", () => SignOutAsync(reader, cancellationToken)).ConfigureAwait(false);
            }

            if (assignmentId is not null)
            {
                await restore.Async("remove the reader's role", async () =>
                    (await TenantAsync(HttpMethod.Delete, $"/api/v1/role-assignments/{assignmentId}", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.NoContent, "remove the role")).ConfigureAwait(false);
            }

            if (personId is not null)
            {
                await restore.Async("deactivate the reader", async () =>
                    (await TenantAsync(HttpMethod.Post, $"/api/v1/users/{personId}/deactivate", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "deactivate the reader")).ConfigureAwait(false);
            }

            await restore.Async("deactivate the readers of earlier runs", () => DeactivateEarlierReadersAsync(admin, cancellationToken)).ConfigureAwait(false);

            if (unitId is not null)
            {
                await restore.Async("deactivate the run's unit", async () =>
                    (await TenantAsync(HttpMethod.Post, $"/api/v1/organisation-units/{unitId}/deactivate", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "deactivate the unit")).ConfigureAwait(false);
            }

            await restore.Async("sign the administrator out", () => SignOutAsync(admin, cancellationToken)).ConfigureAwait(false);
            restore.Finish(finished);
        }
    }

    /// <summary>
    /// A plan applied in the control plane reaches the unit's API: the plan without role transfer refuses role export, the
    /// plan the tenant had gives it back. A tenant without role transfer goes the other way: Enterprise, then its own plan.
    /// The step waits on the session's entitlements and exports once to confirm, since exports have a limit of their own
    /// (20 an hour per person, ADR-0053) that polling the export would spend.
    /// </summary>
    public async Task<string> PlanChangeAsync(CancellationToken cancellationToken)
    {
        var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
        var before = await PlanAsync(cancellationToken).ConfigureAwait(false);
        var restore = new Restore();
        var changed = false;
        var finished = false;
        try
        {
            var hadTransfer = await ExportAllowedAsync(admin, cancellationToken).ConfigureAwait(false);
            var other = hadTransfer ? "standard" : "enterprise";
            await ApplyPlanAsync(other, cancellationToken).ConfigureAwait(false);
            changed = true;
            var first = await WaitAsync(async () => await RoleTransferAsync(admin, cancellationToken).ConfigureAwait(false) != hadTransfer, $"role transfer to be {(hadTransfer ? "off" : "on")} on {other}", cancellationToken).ConfigureAwait(false);
            await WaitAsync(async () => await ExportAllowedAsync(admin, cancellationToken).ConfigureAwait(false) != hadTransfer, $"role export to {(hadTransfer ? "be refused" : "work")} on {other}", cancellationToken).ConfigureAwait(false);
            await ApplyPlanAsync(before, cancellationToken).ConfigureAwait(false);
            changed = false;
            var second = await WaitAsync(async () => await RoleTransferAsync(admin, cancellationToken).ConfigureAwait(false) == hadTransfer, $"role transfer to be {(hadTransfer ? "on" : "off")} on {before} again", cancellationToken).ConfigureAwait(false);
            await WaitAsync(async () => await ExportAllowedAsync(admin, cancellationToken).ConfigureAwait(false) == hadTransfer, $"role export to {(hadTransfer ? "work" : "be refused")} on {before} again", cancellationToken).ConfigureAwait(false);
            finished = true;
            return hadTransfer
                ? $"{other} applied: role export answers {RoleTransferRefused} after {Seconds(first)}; {before} again: it works after {Seconds(second)}"
                : $"{other} applied: role export works after {Seconds(first)}; {before} again: it answers {RoleTransferRefused} after {Seconds(second)}";
        }
        finally
        {
            if (changed)
            {
                await restore.Async($"apply {before} again", () => ApplyPlanAsync(before, cancellationToken)).ConfigureAwait(false);
            }

            await restore.Async("sign the administrator out", () => SignOutAsync(admin, cancellationToken)).ConfigureAwait(false);
            restore.Finish(finished);
        }
    }

    /// <summary>
    /// The import kill switch, turned on for the tenant in the control plane, stops a dry-run import with flags.killed, and
    /// turned off lets it through again. A tenant without role transfer has it for the length of the step.
    /// </summary>
    public async Task<string> KillSwitchAsync(CancellationToken cancellationToken)
    {
        var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
        var restore = new Restore();
        string? planBefore = null;
        var switchedOn = false;
        var finished = false;
        try
        {
            var lent = string.Empty;
            if (!await ExportAllowedAsync(admin, cancellationToken).ConfigureAwait(false))
            {
                planBefore = await PlanAsync(cancellationToken).ConfigureAwait(false);
                await ApplyPlanAsync("enterprise", cancellationToken).ConfigureAwait(false);
                var granted = await WaitAsync(() => ExportAllowedAsync(admin, cancellationToken), "role transfer on enterprise", cancellationToken).ConfigureAwait(false);
                lent = $" (role transfer lent by enterprise after {Seconds(granted)})";
            }

            var start = await ImportAsync(admin, cancellationToken).ConfigureAwait(false);
            if (start.Status != HttpStatusCode.OK)
            {
                throw new SmokeException($"a dry-run import answers {(int)start.Status} {start.Text("error_code")} before the switch; is kill.imports on for {options.Tenant} already?");
            }

            await SwitchAsync(true, cancellationToken).ConfigureAwait(false);
            switchedOn = true;
            var stopped = await WaitAsync(async () => (await ImportAsync(admin, cancellationToken).ConfigureAwait(false)) is { Status: HttpStatusCode.ServiceUnavailable } answer && answer.Text("error_code") == Killed, "the import to be stopped", cancellationToken).ConfigureAwait(false);
            await SwitchAsync(false, cancellationToken).ConfigureAwait(false);
            switchedOn = false;
            var resumed = await WaitAsync(async () => (await ImportAsync(admin, cancellationToken).ConfigureAwait(false)).Status == HttpStatusCode.OK, "the import to work again", cancellationToken).ConfigureAwait(false);
            finished = true;
            return $"kill.imports on: a dry-run import answers 503 {Killed} after {Seconds(stopped)}; off: it works after {Seconds(resumed)}{lent}";
        }
        finally
        {
            if (switchedOn)
            {
                await restore.Async("turn kill.imports off", () => SwitchAsync(false, cancellationToken)).ConfigureAwait(false);
            }

            if (planBefore is not null)
            {
                await restore.Async($"apply {planBefore} again", () => ApplyPlanAsync(planBefore, cancellationToken)).ConfigureAwait(false);
            }

            await restore.Async("sign the administrator out", () => SignOutAsync(admin, cancellationToken)).ConfigureAwait(false);
            restore.Finish(finished);
        }
    }

    private static IEnumerable<JsonElement> Items(Answer answer) => answer.Body!.Value.GetProperty("data").EnumerateArray();

    private static string Seconds(TimeSpan waited) => $"{waited.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s";

    /// <summary>Polls until the condition holds, at most 90 seconds, and answers how long it took.</summary>
    private static async Task<TimeSpan> WaitAsync(Func<Task<bool>> condition, string what, CancellationToken cancellationToken)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            if (await condition().ConfigureAwait(false))
            {
                return TimeProvider.System.GetElapsedTime(started);
            }

            if (TimeProvider.System.GetElapsedTime(started) > WaitLimit)
            {
                throw new SmokeException($"waited {WaitLimit.TotalSeconds:0} s for {what}");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The smoke readers runs before T2 left active, since nobody could be deactivated then: each is deactivated now, so a
    /// tenant the smoke ran on keeps no active test people.
    /// </summary>
    private async Task DeactivateEarlierReadersAsync(string admin, CancellationToken cancellationToken)
    {
        var readers = Uri.EscapeDataString("login_id:startswith:smoke-reader-");
        var active = Uri.EscapeDataString("status:eq:active");
        var left = (await TenantAsync(HttpMethod.Get, $"/api/v1/users?page_size=100&fields=id&filter={readers}&filter={active}", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the readers earlier runs left");
        foreach (var person in Items(left))
        {
            (await TenantAsync(HttpMethod.Post, $"/api/v1/users/{person.GetProperty("id").GetString()}/deactivate", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "deactivate an earlier reader");
        }
    }

    private Task<Answer> TenantAsync(HttpMethod method, string path, string cookie, object? json = null, CancellationToken cancellationToken = default) =>
        api.SendAsync(method, options.IngressUrl, path, host: options.Hostname, cookie: cookie, json: json, cancellationToken: cancellationToken);

    private Task<Answer> ControlAsync(HttpMethod method, string path, object? json = null, CancellationToken cancellationToken = default) =>
        api.SendAsync(method, options.ControlUrl, path, bearer: operatorToken, json: json, cancellationToken: cancellationToken);

    private async Task<string> SignInAsync(string loginId, string password, CancellationToken cancellationToken)
    {
        var answer = (await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/login", host: options.Hostname, json: new { login_id = loginId, password }, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Expect(HttpStatusCode.OK, $"sign in as {loginId}");
        return answer.SetCookie ?? throw new SmokeException($"signing in as {loginId} answered 200 without a session cookie");
    }

    private async Task SignOutAsync(string cookie, CancellationToken cancellationToken)
    {
        var answer = await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/logout", host: options.Hostname, cookie: cookie, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (answer.Status is not (HttpStatusCode.NoContent or HttpStatusCode.OK))
        {
            answer.Expect(HttpStatusCode.NoContent, "sign out");
        }
    }

    /// <summary>Whether the session's entitlements hold role transfer enabled, as the shell reads them.</summary>
    private async Task<bool> RoleTransferAsync(string admin, CancellationToken cancellationToken)
    {
        var context = (await TenantAsync(HttpMethod.Get, "/api/v1/me/context", admin, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Expect(HttpStatusCode.OK, "the session's context");
        return context.Body is { } body
            && body.TryGetProperty("entitlements", out var entitlements)
            && entitlements.TryGetProperty("features", out var features)
            && features.EnumerateArray().Any(f => f.GetProperty("code").GetString() == "authorization.role_transfer" && f.GetProperty("status").GetString() == "enabled");
    }

    private async Task<bool> ExportAllowedAsync(string admin, CancellationToken cancellationToken)
    {
        var answer = await TenantAsync(HttpMethod.Get, "/api/v1/roles/export", admin, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (answer.Status == HttpStatusCode.OK)
        {
            return true;
        }

        if (answer.Status == HttpStatusCode.Forbidden && answer.Text("error_code") == RoleTransferRefused)
        {
            return false;
        }

        throw new SmokeException($"role export answered {(int)answer.Status} {answer.Text("error_code")}; correlation {answer.CorrelationId ?? "none"}");
    }

    private Task<Answer> ImportAsync(string admin, CancellationToken cancellationToken) =>
        TenantAsync(HttpMethod.Post, "/api/v1/roles/import", admin, new { dry_run = true, document = new { format_version = 1, roles = Array.Empty<object>() } }, cancellationToken);

    private async Task<string> PlanAsync(CancellationToken cancellationToken)
    {
        var entitlements = (await ControlAsync(HttpMethod.Get, $"/api/v1/tenants/{options.Tenant}/entitlements", cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the tenant's entitlements");
        return entitlements.Text("plan") ?? throw new SmokeException($"{options.Tenant} has no plan: apply one with make apply-plan TENANT={options.Tenant} PLAN=Enterprise");
    }

    private async Task ApplyPlanAsync(string plan, CancellationToken cancellationToken) =>
        (await ControlAsync(HttpMethod.Post, $"/api/v1/tenants/{options.Tenant}/plan", new { plan = plan.ToLowerInvariant(), dry_run = false }, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, $"apply {plan}");

    private async Task SwitchAsync(bool on, CancellationToken cancellationToken)
    {
        var reason = on ? $"The smoke run {run} stops imports for a minute to prove the switch." : $"The smoke run {run} has proved the switch.";
        (await ControlAsync(HttpMethod.Put, "/api/v1/kill-switches/imports", new { on, reason, tenant = options.Tenant }, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, $"turn kill.imports {(on ? "on" : "off")}");
    }

    /// <summary>What a step restores: each part is tried even when an earlier one fails, and the failures are named together.</summary>
    private sealed class Restore
    {
        private readonly List<string> _failures = [];

        public async Task Async(string what, Func<Task> work)
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _failures.Add($"{what}: {exception.Message}");
            }
        }

        /// <summary>
        /// After a step that passed, a failed restore fails it; after a step that failed, its own failure stands and the
        /// restores that failed are printed beside it, since an exception thrown here would replace it.
        /// </summary>
        public void Finish(bool stepPassed)
        {
            if (_failures.Count == 0)
            {
                return;
            }

            var message = $"could not restore the tenant: {string.Join("; ", _failures)}";
            if (stepPassed)
            {
                throw new SmokeException(message);
            }

            Console.Error.WriteLine($"            {message}");
        }
    }
}
