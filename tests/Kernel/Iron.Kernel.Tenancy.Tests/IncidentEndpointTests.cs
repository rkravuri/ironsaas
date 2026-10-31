using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Events;
using Iron.Kernel.Fleet;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Telemetry;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Tests;

/// <summary>The units' alert copies and the incidents over HTTP, as a unit and an operator call them (TF-079, ADR-0056).</summary>
[Collection(TenancyCollection.Name)]
public class IncidentEndpointTests(TenancyFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly RecordingCopies _copies = new();
    private WebApplication? _app;
    private HttpClient _client = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddIronSecretStore(builder.Configuration);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new FleetModule(), new TenancyModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();
        builder.Services.AddSingleton<IAlertCopies>(_copies);
        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(Cancel);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    [Proves("ADR-0056")]
    public async Task AUnitsCopyIsKeptForItsOwnTenantAndNobodyElseSendsOne()
    {
        var slug = $"copy{Guid.NewGuid():N}"[..20];
        var unit = await GrantAsync(SystemRoles.UnitClient);
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITenantAuditChains>().BindUnitClientAsync(slug, unit, Cancel);
        }

        var operatorId = await GrantAsync(SystemRoles.TenantAdministrator);
        var id = Guid.NewGuid();
        var copy = new
        {
            revision = 2,
            rule = "permission_denials",
            title = "Many permission denials for one principal",
            kind = "threshold",
            severity = "high",
            state = "open",
            group_kind = "principal",
            group_value = Guid.NewGuid().ToString(),
            count = 120L,
            threshold = 100,
            window_minutes = 10,
            first_event_at = DateTimeOffset.UtcNow.AddMinutes(-9),
            last_event_at = DateTimeOffset.UtcNow,
            raised_at = DateTimeOffset.UtcNow,
            acknowledged_at = (DateTimeOffset?)null,
            resolved_at = (DateTimeOffset?)null,
            reason = (string?)null,
            link = "/audit?event_type=authz.permission_denied",
            runbook = "docs/security/incident-response.md#permission-denials",
        };

        using var kept = await Send(HttpMethod.Put, $"/api/v1/alert-copies/{id}", unit, copy);
        using var refused = await Send(HttpMethod.Put, $"/api/v1/alert-copies/{Guid.NewGuid()}", operatorId, copy);

        Assert.Equal(HttpStatusCode.NoContent, kept.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var (tenant, received) = Assert.Single(_copies.Kept);
        Assert.Equal((slug, id, 2, 120L), (tenant, received.Id, received.Revision, received.Count));
    }

    [Fact]
    [Proves("TF-079 AC2", "ADR-0056")]
    public async Task AnOperatorRunsAnIncidentByTheApiFromItsDeclarationToItsReview()
    {
        var tenant = $"inc{Guid.NewGuid():N}"[..20];
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<TenantRegistry>().CreateAsync(new NewTenant(tenant, tenant, [], $"admin@{tenant}.test", null, SizeClass.Small, null, null), Cancel)).IsSuccess);
        }

        var operatorId = await GrantAsync(SystemRoles.TenantAdministrator);
        var reader = await GrantAsync(SystemRoles.ReadOnly);

        using var invalid = await Send(HttpMethod.Post, "/api/v1/incidents", operatorId, new { severity = 9, summary = "x", lead = "y", tenants = Array.Empty<object>() });
        using var declared = await Send(HttpMethod.Post, "/api/v1/incidents", operatorId, new { severity = 1, summary = "Tenant data reached the wrong person", lead = "Olga Operator", communications = "Carl Comms", scribe = "Sam Scribe", tenants = new[] { new { tenant, personal_data = true, known_at = DateTimeOffset.UtcNow.AddHours(-1) } } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Created, declared.StatusCode);
        var id = (await Body(declared)).GetProperty("id").GetString();

        using var note = await Send(HttpMethod.Post, $"/api/v1/incidents/{id}/entries", operatorId, new { kind = "note", text = "The link was revoked.", tenant = (string?)null });
        using var affected = await Send(HttpMethod.Post, $"/api/v1/incidents/{id}/tenants", operatorId, new { tenant, personal_data = true, known_at = (DateTimeOffset?)null });
        using var early = await Send(HttpMethod.Post, $"/api/v1/incidents/{id}/close", operatorId, new { summary = "Contained." });
        using var notice = await Send(HttpMethod.Post, $"/api/v1/incidents/{id}/entries", operatorId, new { kind = "notice", text = "We told your data protection officer.", tenant });
        using var closed = await Send(HttpMethod.Post, $"/api/v1/incidents/{id}/close", operatorId, new { summary = "Contained." });
        using var review = await Send(HttpMethod.Post, $"/api/v1/incidents/{id}/review", operatorId, new { what_happened = "A link went astray.", why = "Links went to any address.", what_changes = "Links go to the tenant's people only." });
        using var read = await Send(HttpMethod.Get, $"/api/v1/incidents/{id}", reader);
        using var listed = await Send(HttpMethod.Get, "/api/v1/incidents?filter=severity:eq:1", operatorId);
        using var noRight = await Send(HttpMethod.Post, $"/api/v1/incidents/{id}/entries", reader, new { kind = "note", text = "x", tenant = (string?)null });
        using var missing = await Send(HttpMethod.Get, $"/api/v1/incidents/{Guid.NewGuid()}", operatorId);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK), (note.StatusCode, affected.StatusCode, early.StatusCode, notice.StatusCode, closed.StatusCode, review.StatusCode));
        var incident = await Body(read);
        Assert.Equal(("closed", "recorded"), (incident.GetProperty("status").GetString(), incident.GetProperty("review_state").GetString()));
        Assert.Equal("notified", incident.GetProperty("tenants").EnumerateArray().Single().GetProperty("notice_state").GetString());
        Assert.Equal(["declared", "note", "tenant", "notice", "closed", "review"], incident.GetProperty("timeline").EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
        Assert.Contains((await Body(listed)).GetProperty("data").EnumerateArray(), i => i.GetProperty("id").GetString() == id && i.GetProperty("reviewed").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, noRight.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task<Guid> GrantAsync(string role)
    {
        var principal = Ids.New();
        await using var scope = _app!.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, role, Cancel)).IsSuccess);
        return principal;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, Guid principal, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderPrincipal.HeaderName, principal.ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return await _client.SendAsync(request, Cancel);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancel)}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();
    }

    /// <summary>The Monitoring module's store as Tenancy hands it the copies.</summary>
    private sealed class RecordingCopies : IAlertCopies
    {
        public ConcurrentQueue<(string Tenant, AlertCopy Copy)> Kept { get; } = new();

        public Task KeepAsync(string tenant, AlertCopy copy, CancellationToken cancellationToken)
        {
            Kept.Enqueue((tenant, copy));
            return Task.CompletedTask;
        }
    }
}
