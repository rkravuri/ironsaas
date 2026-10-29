using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.ControlPlane;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Monitoring.Data;
using Iron.Kernel.Monitoring.Services;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Monitoring.Tests;

/// <summary>The alerts and the rules over HTTP, with the audit search their links lead to (TF-079, ADR-0056).</summary>
[Collection(MonitoringCollection.Name)]
public class MonitoringEndpointTests(MonitoringFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-079 AC1", "ADR-0056")]
    public async Task AnAlertsLinkFindsItsEventsAndAnAdministratorAcknowledgesAndResolvesItWithANote()
    {
        var tenant = await fixture.NewTenantAsync();
        var (time, start) = RuleEvaluationTests.Clock();
        var person = Guid.NewGuid();
        await RuleEvaluationTests.DenialsAsync(tenant, person, start, 100, TimeSpan.FromSeconds(3));
        await tenant.EvaluateAsync(time, start.AddMinutes(6));
        await using var host = await HostAsync(tenant);
        var administrator = await host.AdministratorAsync();

        using var refused = await host.SendAsync(Guid.NewGuid(), HttpMethod.Get, "/api/v1/alerts");
        using var list = await host.SendAsync(administrator, HttpMethod.Get, "/api/v1/alerts?filter=rule:eq:permission_denials");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var alert = (await Body(list)).GetProperty("data").EnumerateArray().Single();
        var id = alert.GetProperty("id").GetString();
        Assert.Equal(person.ToString(), alert.GetProperty("group_value").GetString());
        Assert.Equal(100, alert.GetProperty("count").GetInt64());
        Assert.StartsWith("/audit?event_type=authz.permission_denied", alert.GetProperty("link").GetString(), StringComparison.Ordinal);

        // The link's search on the API finds exactly the events the alert counted.
        using var search = await host.SendAsync(administrator, HttpMethod.Get, alert.GetProperty("search").GetString()!);
        var events = (await Body(search)).GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(100, events.Count);
        Assert.All(events, e => Assert.Equal((AuthorizationAuditEvents.PermissionDenied, person.ToString()), (e.GetProperty("event_type").GetString(), e.GetProperty("actor_id").GetString())));

        using var acknowledged = await host.SendAsync(administrator, HttpMethod.Post, $"/api/v1/alerts/{id}/acknowledge", new { note = (string?)null });
        using var noNote = await host.SendAsync(administrator, HttpMethod.Post, $"/api/v1/alerts/{id}/resolve", new { note = " " });
        using var resolved = await host.SendAsync(administrator, HttpMethod.Post, $"/api/v1/alerts/{id}/resolve", new { note = "A load test of the integration team." });
        using var again = await host.SendAsync(administrator, HttpMethod.Post, $"/api/v1/alerts/{id}/resolve", new { note = "Once more." });
        using var one = await host.SendAsync(administrator, HttpMethod.Get, $"/api/v1/alerts/{id}");
        using var none = await host.SendAsync(administrator, HttpMethod.Get, $"/api/v1/alerts/{Guid.NewGuid()}");

        Assert.Equal("acknowledged", (await Body(acknowledged)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, noNote.StatusCode);
        Assert.Equal("resolved", (await Body(resolved)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("A load test of the integration team.", (await Body(one)).GetProperty("note").GetString());
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    [Fact]
    [Proves("TF-079 AC4", "ADR-0056")]
    public async Task TheRulesShowTheirValuesAndStateAndATenantsValueIsStricterOrRefused()
    {
        var tenant = await fixture.NewTenantAsync();
        tenant.Configuration["Monitoring:Rules:lockouts_by_address:Threshold"] = "none";
        var (time, start) = RuleEvaluationTests.Clock();
        await tenant.EvaluateAsync(time, start);
        await using var host = await HostAsync(tenant);
        var administrator = await host.AdministratorAsync();

        using var rules = await host.SendAsync(administrator, HttpMethod.Get, "/api/v1/alert-rules");
        var body = await Body(rules);
        var data = body.GetProperty("data").EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!);
        Assert.Equal(JsonValueKind.String, body.GetProperty("evaluated_at").ValueKind);
        Assert.Equal(("running", 100), (data["permission_denials"].GetProperty("state").GetString(), data["permission_denials"].GetProperty("effective").GetProperty("threshold").GetInt32()));
        Assert.Equal("failing", data["lockouts_by_address"].GetProperty("state").GetString());
        Assert.Contains("'none'", data["lockouts_by_address"].GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.True(data["impersonations"].GetProperty("at_once").GetBoolean());

        using var looser = await host.SendAsync(administrator, HttpMethod.Put, "/api/v1/alert-rules/permission_denials", new { threshold = 500, window_minutes = (int?)null, above = (long?)null });
        using var stricter = await host.SendAsync(administrator, HttpMethod.Put, "/api/v1/alert-rules/permission_denials", new { threshold = 25, window_minutes = 60, above = (long?)null });
        using var failing = await host.SendAsync(administrator, HttpMethod.Put, "/api/v1/alert-rules/lockouts_by_address", new { threshold = 2, window_minutes = (int?)null, above = (long?)null });
        using var unknown = await host.SendAsync(administrator, HttpMethod.Put, "/api/v1/alert-rules/no_such_rule", new { threshold = 2, window_minutes = (int?)null, above = (long?)null });

        Assert.Equal(HttpStatusCode.BadRequest, looser.StatusCode);
        var set = await Body(stricter);
        Assert.Equal((25, 60), (set.GetProperty("effective").GetProperty("threshold").GetInt32(), set.GetProperty("effective").GetProperty("window_minutes").GetInt32()));
        Assert.Equal(HttpStatusCode.Conflict, failing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    [Proves("ADR-0056")]
    public async Task TheControlPlaneSendsOpenAlertsToAlertmanagerAtEachPassAndAnEndedOneOnceWithItsEnd()
    {
        var control = await fixture.NewTenantAsync(MonitoringScope.ControlPlane);
        var (time, start) = RuleEvaluationTests.Clock();
        await control.WriteAsync(time, start, new Iron.Kernel.Audit.Contracts.AuditEvent("tenancy.break_glass_requested", "success", Guid.NewGuid(), "echo", RecordType: "break_glass", RecordId: Guid.NewGuid()));
        await control.EvaluateAsync(time, start.AddMinutes(1));
        var alertmanager = new AlertmanagerStandIn();

        Assert.Equal(1, await ForwardAsync(control, time, alertmanager));
        Assert.Equal(1, await ForwardAsync(control, time, alertmanager));
        var posted = JsonDocument.Parse(alertmanager.Bodies[0]).RootElement.EnumerateArray().Single();
        Assert.Equal(("break_glass_requests", "control", "critical"), (posted.GetProperty("labels").GetProperty("alertname").GetString(), posted.GetProperty("labels").GetProperty("tenant").GetString(), posted.GetProperty("labels").GetProperty("severity").GetString()));
        Assert.False(posted.TryGetProperty("endsAt", out _), "a firing alert has no end");

        // Resolved, it goes once more with its end, then no more; a refusal leaves it to be sent again.
        await using (var provider = control.Build(time))
        await using (var scope = provider.CreateAsyncScope())
        {
            var alert = await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Alerts.SingleAsync(a => a.Rule == "break_glass_requests", Cancel);
            Assert.True((await scope.ServiceProvider.GetRequiredService<AlertService>().ResolveAsync(alert.Id, "An approved break-glass for the migration.", Cancel)).IsSuccess);
        }

        alertmanager.Status = HttpStatusCode.InternalServerError;
        Assert.Equal(0, await ForwardAsync(control, time, alertmanager));
        alertmanager.Status = HttpStatusCode.OK;
        Assert.Equal(1, await ForwardAsync(control, time, alertmanager));
        Assert.True(JsonDocument.Parse(alertmanager.Bodies[^1]).RootElement.EnumerateArray().Single().TryGetProperty("endsAt", out _));
        Assert.Equal(0, await ForwardAsync(control, time, alertmanager));
        Assert.Equal(0, await new AlertmanagerForwarder(null!, new HttpClient(alertmanager), new AlertmanagerOptions(), NullLogger<AlertmanagerForwarder>.Instance).SendAsync(Cancel));
    }

    private static async Task<int> ForwardAsync(MonitoredTenant control, FakeTimeProvider time, AlertmanagerStandIn alertmanager)
    {
        await using var provider = control.Build(time);
        await using var scope = provider.CreateAsyncScope();
        using var http = new HttpClient(alertmanager, disposeHandler: false);
        var forwarder = new AlertmanagerForwarder(scope.ServiceProvider.GetRequiredService<MonitoringDbContext>(), http, new AlertmanagerOptions { Address = "http://alertmanager.test" }, NullLogger<AlertmanagerForwarder>.Instance);
        return await forwarder.SendAsync(Cancel);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancel)}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();
    }

    private static async Task<TestApi> HostAsync(MonitoredTenant tenant)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(tenant.Configuration);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddSingleton(new TenantContext(TenantId.Parse("acme"), ["acme.localhost"]));
        var options = new ControlPlaneOptions();
        builder.Services.AddSingleton(options);
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(tenant.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new AuthorizationModule(), new MonitoringModule(), new DeclaredTypesModule()]);
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

    /// <summary>Alertmanager's API as the control plane meets it: each post kept, answered with the status set.</summary>
    private sealed class AlertmanagerStandIn : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v2/alerts", request.RequestUri!.AbsolutePath);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(Status) { Content = new StringContent("", Encoding.UTF8, "application/json") };
        }
    }
}
