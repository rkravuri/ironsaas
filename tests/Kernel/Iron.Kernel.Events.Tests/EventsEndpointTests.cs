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
using Iron.Kernel.Files.Contracts;
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

namespace Iron.Kernel.Events.Tests;

/// <summary>The events' endpoints (TF-112, ADR-0060): searched, inspected and replayed with the module's permissions only.</summary>
[Collection(EventsCollection.Name)]
public class EventsEndpointTests(EventsFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-112 AC3")]
    public async Task SomeoneWithoutTheEventsPermissionIsRefused()
    {
        var tenant = await fixture.NewTenantAsync();
        await using var api = await HostAsync(tenant);
        var nobody = Ids.New();

        foreach (var path in new[] { "/api/v1/events", "/api/v1/events/dead-letters", "/api/v1/events/types", $"/api/v1/events/{Ids.New()}" })
        {
            using var refused = await api.SendAsync(nobody, HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("authz.permission_denied", (await Body(refused, expectSuccess: false)).GetProperty("error_code").GetString());
        }

        using var replay = await api.SendAsync(nobody, HttpMethod.Post, "/api/v1/events/replay", new { subscriber = ProbeModule.Counter, from_position = 1, reason = "Why not." });
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
    }

    [Fact]
    [Proves("ADR-0060")]
    public async Task AnAdministratorSearchesARecordsEventsWithTheirSubscribersAndSeesTheDeclaredEvents()
    {
        var tenant = await fixture.NewTenantAsync();
        await using var api = await HostAsync(tenant);
        var administrator = await api.AdministratorAsync();
        var order = Ids.New();
        await EventsTenant.PublishAsync(api.Services, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()));
        await EventsTenant.PublishAsync(api.Services, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 2, Ids.New()));
        await EventsTenant.PublishAsync(api.Services, ProbeModule.PlacedV2, order, new OrderPlacedV2(order, "PO-1", 2, Ids.New(), null));
        await EventsTenant.DrainAsync(api.Services);

        using var listed = await api.SendAsync(administrator, HttpMethod.Get, $"/api/v1/events?filter=record_id:eq:{order}");
        var data = (await Body(listed)).GetProperty("data");
        Assert.Equal(3, data.GetArrayLength());
        var newest = data[0];
        Assert.Equal((2, "no_subscriber"), (newest.GetProperty("version").GetInt32(), newest.GetProperty("status").GetString()));
        var older = data[1];
        Assert.Equal("handled", older.GetProperty("status").GetString());
        Assert.Equal([ProbeModule.Counter, ProbeModule.Flaky, ProbeModule.Sequencer], older.GetProperty("deliveries").EnumerateArray().Select(d => d.GetProperty("subscriber").GetString()));
        Assert.Equal(order, older.GetProperty("data").GetProperty("order_id").GetGuid());

        using var one = await api.SendAsync(administrator, HttpMethod.Get, $"/api/v1/events/{older.GetProperty("id").GetGuid()}");
        Assert.Equal(3, (await Body(one)).GetProperty("deliveries").GetArrayLength());

        using var types = await api.SendAsync(administrator, HttpMethod.Get, "/api/v1/events/types");
        var probe = (await Body(types)).EnumerateArray().Where(t => t.GetProperty("name").GetString() == "probe.order.placed").ToList();
        Assert.Equal([1, 2], probe.Select(t => t.GetProperty("version").GetInt32()));
        Assert.Equal("2027-01-31", probe[0].GetProperty("deprecated_on").GetString());
        Assert.Equal(3, probe[0].GetProperty("subscribers").GetArrayLength());
    }

    [Fact]
    [Proves("ADR-0060")]
    public void TheReplayAsksForARecentSignInAndTheReplayPermission()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase("Host=localhost;Database=none");
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new SettingsModule()]);
        var app = builder.Build();
        app.MapModules();
        var replay = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == "/api/v1/events/replay");

        Assert.NotNull(replay.Metadata.GetMetadata<RecentAuthenticationMetadata>());
        Assert.Contains(replay.Metadata.GetOrderedMetadata<PermissionMetadata>(), m => m.Permission == Contracts.EventsPermissions.EventsReplay);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response, bool expectSuccess = true)
    {
        if (expectSuccess)
        {
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancel)}");
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();
    }

    private static async Task<TestApi> HostAsync(EventsTenant tenant)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(tenant.Configuration);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddSingleton(new TenantContext(TenantId.Parse("acme"), ["acme.localhost"]));
        builder.Services.AddSingleton(tenant.Behaviour);
        builder.Services.AddSingleton<IObjectStorage>(tenant.Storage);
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(tenant.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new SettingsModule(), new ProbeModule()]);
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
