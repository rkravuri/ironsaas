using System.Net;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Files;
using Iron.Kernel.Files.Contracts;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Audit.Tests;

/// <summary>A denial over HTTP becomes a row, and the administrator finds it through the search endpoint with the response's own correlation id.</summary>
[Collection(AuditCollection.Name)]
public class AuditEventsEndpointTests(AuditFixture fixture) : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.AppConnectionString, AuditFixture.AppRole);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = fixture.StorageConnectionString,
            ["Storage:Container"] = AuditFixture.Container,
        });
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new AuthorizationModule(), new FilesModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
        await using var scope = _app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IObjectStorage>().EnsureContainerAsync(TestContext.Current.CancellationToken);
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
    [Proves("TF-031 AC1", "TF-064 AC3", "ADR-0013")]
    public async Task ADenialBecomesARowThatTheAdministratorFindsByCorrelationId()
    {
        var alice = Ids.New();
        var bob = Ids.New();
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var assignments = scope.ServiceProvider.GetRequiredService<IRoleAssignments>();
            Assert.True((await assignments.AssignAsync(alice, SystemRoles.TenantAdministrator, TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var denied = await Send(bob, "/api/v1/audit-events");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var correlation = Assert.Single(denied.Headers.GetValues(CorrelationId.HeaderName));

        using var found = await Send(alice, $"/api/v1/audit-events?filter=correlation_id:eq:{correlation}");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        using var body = JsonDocument.Parse(await found.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var row = Assert.Single(body.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal("authz.permission_denied", row.GetProperty("event_type").GetString());
        Assert.Equal(AuditOutcome.Denied, row.GetProperty("outcome").GetString());
        Assert.Equal(bob, row.GetProperty("actor_id").GetGuid());
        Assert.Equal(bob, row.GetProperty("created_by").GetGuid());
        Assert.Equal(AuditSource.Api, row.GetProperty("source").GetString());
        Assert.Equal("GET /api/v1/audit-events", row.GetProperty("target").GetString());
        Assert.Equal(AuditPermissions.EventsRead.Name, row.GetProperty("details").GetProperty("permission").GetString());
    }

    [Fact]
    [Proves("ADR-0012")]
    public async Task NobodySignedInGets401()
    {
        using var response = await Send(null, "/api/v1/audit-events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PermissionErrors.NotAuthenticated, problem.RootElement.GetProperty("error_code").GetString());
    }

    [Fact]
    [Proves("TF-064 AC1")]
    public async Task ASearchByRecordShowsAllItsEventsInTimeOrder()
    {
        var (reader, recordId) = await ReaderWithEventsAsync("tests.record.step", 3);

        using var found = await Send(reader, $"/api/v1/audit-events?filter=record_type:eq:tests_record&filter=record_id:eq:{recordId}&sort=created_at");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        using var body = JsonDocument.Parse(await found.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var rows = body.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(["step 0", "step 1", "step 2"], rows.Select(r => r.GetProperty("target").GetString()));
        var times = rows.Select(r => r.GetProperty("created_at").GetDateTimeOffset()).ToList();
        Assert.Equal(times.Order(), times);
    }

    [Fact]
    [Proves("ADR-0050")]
    public async Task AnExportWritesTheSearchToAFileWithItsFiltersFirstAndIsItselfAudited()
    {
        var (reader, recordId) = await ReaderWithEventsAsync("tests.export.row", 2);

        using var created = await Send(reader, $"/api/v1/audit-events/exports?filter=record_id:eq:{recordId}&sort=created_at", HttpMethod.Post);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var answer = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, answer.RootElement.GetProperty("rows").GetInt32());
        using var file = await Send(reader, answer.RootElement.GetProperty("download_url").GetString()!);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        var lines = (await file.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("# Audit trail export, ", lines[0], StringComparison.Ordinal);
        Assert.Equal($"# Filters: record_id:eq:{recordId}", lines[1]);
        Assert.StartsWith("id,created_at,event_type,", lines[2], StringComparison.Ordinal);
        Assert.Equal(2, lines.Skip(3).Count(l => l.Contains("tests.export.row", StringComparison.Ordinal)));

        using var audited = await Send(reader, $"/api/v1/audit-events?filter=event_type:eq:{AuditEvents.EventsExported}&filter=actor_id:eq:{reader}");
        using var events = JsonDocument.Parse(await audited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var exported = Assert.Single(events.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(answer.RootElement.GetProperty("id").GetString(), exported.GetProperty("details").GetProperty("export_id").GetString());
    }

    [Fact]
    [Proves("ADR-0050")]
    public async Task ALegalHoldNeedsAReasonAndItsPlacingAndReleaseAreAuditedAtHighSeverity()
    {
        var (reader, recordId) = await ReaderWithEventsAsync("tests.hold.record", 1);

        using var refused = await Send(reader, "/api/v1/legal-holds", HttpMethod.Post, new { scope = "record", record_type = "tests_record", record_id = recordId, reason = " " });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var placed = await Send(reader, "/api/v1/legal-holds", HttpMethod.Post, new { scope = "record", record_type = "tests_record", record_id = recordId, reason = "Litigation 2026-17" });
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        using var hold = JsonDocument.Parse(await placed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var holdId = hold.RootElement.GetProperty("id").GetString();
        using var released = await Send(reader, $"/api/v1/legal-holds/{holdId}/release", HttpMethod.Post, new { reason = "Settled" });
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);

        using var audited = await Send(reader, $"/api/v1/audit-events?filter=record_id:eq:{recordId}&filter=severity:eq:high&sort=created_at");
        using var events = JsonDocument.Parse(await audited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [(AuditEvents.LegalHoldPlaced, "Litigation 2026-17"), (AuditEvents.LegalHoldReleased, "Settled")],
            events.RootElement.GetProperty("data").EnumerateArray().Select(e => (e.GetProperty("event_type").GetString(), e.GetProperty("reason").GetString())));
    }

    /// <summary>A tenant administrator, and a record of the test's own with events written one after another.</summary>
    private async Task<(Guid Reader, Guid RecordId)> ReaderWithEventsAsync(string eventType, int count)
    {
        var reader = Ids.New();
        var recordId = Ids.New();
        await using var scope = _app!.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(reader, SystemRoles.TenantAdministrator, TestContext.Current.CancellationToken)).IsSuccess);
        var writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        for (var i = 0; i < count; i++)
        {
            await writer.WriteAsync(new AuditEvent(eventType, AuditOutcome.Success, reader, $"step {i}", RecordType: "tests_record", RecordId: recordId), TestContext.Current.CancellationToken);
        }

        return (reader, recordId);
    }

    private async Task<HttpResponseMessage> Send(Guid? principal, string path, HttpMethod? method = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = System.Net.Http.Json.JsonContent.Create(body, options: new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
        }

        if (principal is not null)
        {
            request.Headers.Add(HeaderPrincipal.HeaderName, principal.Value.ToString());
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
