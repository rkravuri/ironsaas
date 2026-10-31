using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Events;
using Iron.Kernel.Events.Services;
using Iron.Kernel.Jobs.Contracts;
using Iron.Kernel.Jobs.Services;
using Iron.Kernel.Settings;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Jobs.Tests;

/// <summary>The job console (TF-121, ADR-0061): its endpoints with the module's permissions, and pause, resume, run now and run again, audited.</summary>
[Collection(JobsCollection.Name)]
public class ConsoleTests(JobsFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-121 AC1", "ADR-0061")]
    public async Task AFailedJobShowsItsErrorAndItsRunLog()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await HostAsync(tenant, time);
        var administrator = await api.AdministratorAsync();
        var id = await JobsTenant.StartAsync(api.Services, ProbeJobsModule.Slow, "logged");
        await JobsTenant.Runner(api.Services).RunOnceAsync(Cancel);

        using var job = await api.SendAsync(administrator, HttpMethod.Get, $"/api/v1/jobs/{ProbeJobsModule.Slow.Name}");
        var failure = (await Body(job)).GetProperty("last_failure");
        Assert.Equal(id, failure.GetProperty("id").GetGuid());
        Assert.StartsWith("Timed out after 300ms", failure.GetProperty("error").GetString(), StringComparison.Ordinal);

        using var run = await api.SendAsync(administrator, HttpMethod.Get, $"/api/v1/jobs/runs/{id}");
        var body = await Body(run);
        Assert.Contains("started, waiting", body.GetProperty("log").GetString(), StringComparison.Ordinal);
        Assert.Equal("queued", body.GetProperty("status").GetString());

        using var runs = await api.SendAsync(administrator, HttpMethod.Get, $"/api/v1/jobs/{ProbeJobsModule.Slow.Name}/runs");
        Assert.Equal(1, (await Body(runs)).GetProperty("data").GetArrayLength());
    }

    [Fact]
    [Proves("TF-121 AC2", "ADR-0061")]
    public async Task APausedJobSkipsItsRunsUntilResumedItsStateVisibleAndBothAudited()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await HostAsync(tenant, time);
        var administrator = await api.AdministratorAsync();
        await JobsTenant.DrainAsync(api.Services);
        Assert.Equal(1, tenant.Behaviour.Count(ProbeJobsModule.EveryMinute.Name));

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var paused = await scope.ServiceProvider.GetRequiredService<JobConsole>().PauseAsync(ProbeJobsModule.EveryMinute.Name, "The tenant's import runs tonight.", null, Cancel);
            Assert.True(paused.IsSuccess, paused.ToString());
        }

        using (var job = await api.SendAsync(administrator, HttpMethod.Get, $"/api/v1/jobs/{ProbeJobsModule.EveryMinute.Name}"))
        {
            var body = await Body(job);
            Assert.Equal(("paused", "The tenant's import runs tonight."), (body.GetProperty("state").GetString(), body.GetProperty("pause_reason").GetString()));
            Assert.Equal(JsonValueKind.Null, body.GetProperty("next_run_at").ValueKind);
        }

        for (var minute = 0; minute < 3; minute++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await JobsTenant.DrainAsync(api.Services);
        }

        Assert.Equal(1, tenant.Behaviour.Count(ProbeJobsModule.EveryMinute.Name));

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var resumed = await scope.ServiceProvider.GetRequiredService<JobConsole>().ResumeAsync(ProbeJobsModule.EveryMinute.Name, "The import is done.", null, Cancel);
            Assert.Equal(JobStates.Active, resumed.Value.State);
        }

        time.Advance(TimeSpan.FromMinutes(1));
        await JobsTenant.DrainAsync(api.Services);
        Assert.Equal(2, tenant.Behaviour.Count(ProbeJobsModule.EveryMinute.Name));
        Assert.Equal(0, await tenant.AuditCountAsync(JobsAuditEvents.ScheduleMissed, ProbeJobsModule.EveryMinute.Name));
        Assert.Equal(1, await tenant.AuditCountAsync(JobsAuditEvents.JobPaused, ProbeJobsModule.EveryMinute.Name));
        Assert.Equal(1, await tenant.AuditCountAsync(JobsAuditEvents.JobResumed, ProbeJobsModule.EveryMinute.Name));
    }

    [Fact]
    [Proves("TF-121 AC3")]
    public async Task SomeoneWithoutTheJobsPermissionIsRefused()
    {
        var tenant = await fixture.NewTenantAsync();
        await using var api = await HostAsync(tenant, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var nobody = Ids.New();

        foreach (var path in new[] { "/api/v1/jobs", $"/api/v1/jobs/{ProbeJobsModule.EveryMinute.Name}", $"/api/v1/jobs/{ProbeJobsModule.EveryMinute.Name}/runs", $"/api/v1/jobs/runs/{Ids.New()}", "/api/v1/dead-letters" })
        {
            using var refused = await api.SendAsync(nobody, HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("authz.permission_denied", (await Body(refused, expectSuccess: false)).GetProperty("error_code").GetString());
        }

        using var run = await api.SendAsync(nobody, HttpMethod.Post, $"/api/v1/jobs/{ProbeJobsModule.Explicit.Name}/run", new { reason = "Why not." });
        Assert.Equal(HttpStatusCode.Forbidden, run.StatusCode);
    }

    [Fact]
    [Proves("ADR-0061")]
    public async Task ChangingAJobAsksForTheManagePermissionAndARecentSignIn()
    {
        var tenant = await fixture.NewTenantAsync();
        await using var api = await HostAsync(tenant, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var endpoints = ((IEndpointRouteBuilder)api.App).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1/jobs", StringComparison.Ordinal) && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"))
            .ToList();

        Assert.Equal(4, endpoints.Count);
        Assert.All(endpoints, e =>
        {
            Assert.NotNull(e.Metadata.GetMetadata<RecentAuthenticationMetadata>());
            Assert.Contains(e.Metadata.GetOrderedMetadata<PermissionMetadata>(), m => m.Permission == JobsPermissions.JobsManage);
        });
    }

    [Fact]
    [Proves("ADR-0061")]
    public async Task ARunStartedNowAndADeadLetterRunAgainAfterAFixRunWithTheirReasonsInTheTrail()
    {
        var tenant = await fixture.NewTenantAsync();
        tenant.Behaviour.FlakyFailures = -1;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var administrator = Ids.New();
        await using var worker = tenant.Build(time, administrator);
        RunView started;
        await using (var scope = worker.CreateAsyncScope())
        {
            var console = scope.ServiceProvider.GetRequiredService<JobConsole>();
            Assert.Equal(JobsErrors.ReasonRequired, (await console.TriggerAsync(ProbeJobsModule.Flaky.Name, " ", null, Cancel)).Error.Code);
            Assert.Equal(JobsErrors.JobEachProcess, (await console.TriggerAsync(ProbeJobsModule.EachProcess.Name, "Now.", null, Cancel)).Error.Code);
            started = (await console.TriggerAsync(ProbeJobsModule.Flaky.Name, "Checking the flaky probe.", null, Cancel)).Value;
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await JobsTenant.Runner(worker).RunOnceAsync(Cancel);
            time.Advance(TimeSpan.FromMinutes(2));
        }

        var dead = await JobsTenant.RunAsync(worker, started.Id);
        Assert.Equal((RunStates.Dead, administrator, "Checking the flaky probe."), (dead.Status, dead.TriggeredBy, dead.Reason));

        tenant.Behaviour.FlakyFailures = 0;
        await using (var scope = worker.CreateAsyncScope())
        {
            var console = scope.ServiceProvider.GetRequiredService<JobConsole>();
            var rerun = await console.RerunAsync(started.Id, "The probe is fixed (ticket 21).", null, Cancel);
            Assert.Equal((RunStates.Queued, 1), (rerun.Value.Status, rerun.Value.Reruns));
            Assert.Equal(JobsErrors.RunNotFailed, (await console.RerunAsync(started.Id, "Again.", null, Cancel)).Error.Code);
        }

        await JobsTenant.DrainAsync(worker);
        Assert.Equal((RunStates.Succeeded, 1), (await JobsTenant.RunAsync(worker, started.Id)) is var run ? (run.Status, run.Attempts) : default);
        Assert.Equal(1, await tenant.AuditCountAsync(JobsAuditEvents.RunTriggered, ProbeJobsModule.Flaky.Name));
        Assert.Equal(1, await tenant.AuditCountAsync(JobsAuditEvents.RunRerun, ProbeJobsModule.Flaky.Name));
        Assert.Equal(1, await tenant.AuditCountAsync(JobsAuditEvents.RunDeadLettered, ProbeJobsModule.Flaky.Name));
    }

    [Fact]
    [Proves("ADR-0061", "ADR-0060")]
    public async Task TheDeadLettersOfJobsAndEventsAreListedTogetherNewestFirstAndEventsOnlyForTheirReaders()
    {
        var tenant = await fixture.NewTenantAsync();
        tenant.Behaviour.FlakyFailures = -1;
        tenant.Behaviour.StartFails = true;
        tenant.Configuration["Events:Dispatch:Attempts"] = "1";
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await HostAsync(tenant, time);
        var administrator = await api.AdministratorAsync();

        var run = await JobsTenant.StartAsync(api.Services, ProbeJobsModule.Flaky, "dead-job");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await JobsTenant.Runner(api.Services).RunOnceAsync(Cancel);
            time.Advance(TimeSpan.FromMinutes(2));
        }

        var thing = Ids.New();
        var deadEvent = await JobsTenant.PublishAsync(api.Services, ProbeJobsModule.Happened, thing, new ThingHappened(thing));
        await api.Services.GetRequiredService<EventDispatcher>().RunOnceAsync(Cancel);

        using var listed = await api.SendAsync(administrator, HttpMethod.Get, "/api/v1/dead-letters");
        var body = await Body(listed);
        Assert.Equal(2, body.GetProperty("meta").GetProperty("total_count").GetInt32());
        var rows = body.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(["event", "job"], rows.Select(r => r.GetProperty("kind").GetString()));
        Assert.Equal((deadEvent, "probe.start_on_thing"), (rows[0].GetProperty("id").GetGuid(), rows[0].GetProperty("name").GetString()));
        Assert.Equal((run, ProbeJobsModule.Flaky.Name), (rows[1].GetProperty("id").GetGuid(), rows[1].GetProperty("name").GetString()));

        await using var scope = api.Services.CreateAsyncScope();
        var jobsOnly = await scope.ServiceProvider.GetRequiredService<JobConsole>().DeadLettersAsync(new PageRequest(1, 50), withEvents: false, Cancel);
        Assert.Equal(["job"], jobsOnly.Items.Select(d => d.Kind));
    }

    [Fact]
    [Proves("ADR-0061", "ADR-0060")]
    public async Task AnEventStartsItsJobOnceEvenWhenItIsDeliveredAgain()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var worker = tenant.Build(time);
        var thing = Ids.New();
        var id = await JobsTenant.PublishAsync(worker, ProbeJobsModule.Happened, thing, new ThingHappened(thing));
        var dispatcher = worker.GetRequiredService<EventDispatcher>();
        await dispatcher.RunOnceAsync(Cancel);
        await JobsTenant.DrainAsync(worker);

        // A replay of the handled event, and the job started again by the same key: still one run.
        await using (var scope = worker.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventInspection>().ReplayAsync(new ReplayRequest("probe.start_on_thing", [id], null, null, "A test of the key."), null, Cancel);
        }

        await dispatcher.RunOnceAsync(Cancel);
        await JobsTenant.StartAsync(worker, ProbeJobsModule.FromEvent, $"event:{id:D}");
        await JobsTenant.DrainAsync(worker);

        var run = Assert.Single(await JobsTenant.RunsAsync(worker, ProbeJobsModule.FromEvent.Name));
        Assert.Equal(($"event:{id:D}", thing.ToString("D"), RunStates.Succeeded), (run.Key, run.Payload, run.Status));
        Assert.Equal(1, tenant.Behaviour.Count(ProbeJobsModule.FromEvent.Name));
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response, bool expectSuccess = true)
    {
        if (expectSuccess)
        {
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancel)}");
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();
    }

    private static async Task<TestApi> HostAsync(JobsTenant tenant, TimeProvider time)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(tenant.Configuration);
        builder.Services.AddSingleton(time);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddSingleton(new TenantContext(TenantId.Parse("acme"), ["acme.localhost"]));
        builder.Services.AddSingleton(tenant.Behaviour);
        builder.Services.AddSingleton<ITenantStanding>(tenant.Standing);
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(tenant.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new SettingsModule(), new JobsModule(), new ProbeJobsModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();
        var app = builder.Build();
        app.UseIronTelemetry();
        app.UseIronApi();
        app.MapModules();
        await app.StartAsync(Cancel);
        return new TestApi(app);
    }

    private sealed class TestApi(WebApplication app) : IAsyncDisposable
    {
        private readonly HttpClient _client = app.GetTestClient();

        public IServiceProvider Services => app.Services;

        public WebApplication App => app;

        public async Task<Guid> AdministratorAsync()
        {
            var id = Ids.New();
            await using var scope = app.Services.CreateAsyncScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(id, SystemRoles.TenantAdministrator, Cancel)).IsSuccess);
            return id;
        }

        public async Task<HttpResponseMessage> SendAsync(Guid principal, HttpMethod method, string path, object? body = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            request.Headers.Add(HeaderPrincipal.HeaderName, principal.ToString());
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json);
            }

            return await _client.SendAsync(request, Cancel);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class HeaderPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
    {
        public const string HeaderName = "X-Test-Principal";

        public Guid Id => Guid.TryParse(accessor.HttpContext?.Request.Headers[HeaderName].ToString(), out var id) ? id : Guid.Empty;
    }

    private sealed class AllEntitled : IEntitlementGate, IFlagGate
    {
        public ValueTask<EntitlementDecision> CheckAsync(EntitlementRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(EntitlementDecision.Allow);

        public ValueTask<FlagDecision> CheckAsync(FlagRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(FlagDecision.Allow);
    }
}
