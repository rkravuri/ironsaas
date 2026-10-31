using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Events;
using Iron.Kernel.Fleet;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Telemetry;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iron.Kernel.Tenancy.Tests;

/// <summary>
/// The tenants' audit chains in the control plane (TF-061, ADR-0050): only a tenant's own unit client sends its anchors,
/// always for its own tenant; an anchor is never changed, by the endpoint or by the app role; a verification is kept and,
/// when it did not pass, raises a high-severity event; a tenant is due daily, in full monthly, and at once when asked.
/// </summary>
[Collection(TenancyCollection.Name)]
public class AuditChainEndpointTests(TenancyFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

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
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddIronSecretStore(builder.Configuration);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new FleetModule(), new TenancyModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();
        builder.Services.AddScoped<IRecentAuthentication, HeaderRecentAuthentication>();
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
    [Proves("ADR-0050")]
    public async Task OnlyATenantsUnitClientSendsAnchorsAlwaysForItsOwnTenantAndNeverChangesOne()
    {
        var slug = $"anchor-{Ids.New():N}"[..20];
        var unit = await UnitClientAsync(slug);
        var hash = new string('a', 64);

        using (var created = await Send(HttpMethod.Post, "/api/v1/audit-anchors", unit, new { kind = "seal", position = 12, hash, at = DateTimeOffset.UtcNow }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using (var again = await Send(HttpMethod.Post, "/api/v1/audit-anchors", unit, new { kind = "seal", position = 12, hash, at = DateTimeOffset.UtcNow }))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        using (var rewritten = await Send(HttpMethod.Post, "/api/v1/audit-anchors", unit, new { kind = "seal", position = 12, hash = new string('b', 64), at = DateTimeOffset.UtcNow }))
        {
            Assert.Equal(HttpStatusCode.Conflict, rewritten.StatusCode);
            Assert.Equal(TenancyErrors.AuditAnchorConflict, await ErrorCode(rewritten));
        }

        // An operator holds the permission through the administrator role, but is no unit: nothing is anyone's anchor.
        var operatorId = await GrantAsync(SystemRoles.TenantAdministrator);
        using (var notAUnit = await Send(HttpMethod.Post, "/api/v1/audit-anchors", operatorId, new { kind = "seal", position = 13, hash, at = DateTimeOffset.UtcNow }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, notAUnit.StatusCode);
            Assert.Equal(TenancyErrors.AuditAnchorNotAUnit, await ErrorCode(notAUnit));
        }

        using var latest = await Send(HttpMethod.Get, "/api/v1/audit-anchors/latest", unit);
        using var body = JsonDocument.Parse(await latest.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(12, body.RootElement.GetProperty("seal").GetProperty("position").GetInt64());
        Assert.Equal(hash, body.RootElement.GetProperty("seal").GetProperty("hash").GetString());
        await using var scope = _app!.Services.CreateAsyncScope();
        var conflict = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .SingleAsync(e => e.EventType == TenancyAuditEvents.AuditAnchorConflict && e.Target == slug, Cancel);
        Assert.Equal(AuditSeverity.High, conflict.Severity);
    }

    [Fact]
    [Proves("TF-067 AC1", "ADR-0052")]
    public async Task AUnitReadsItsOwnVerificationsOfAPeriodAndItsLatestAnchorWithItsIdForAnEvidencePack()
    {
        var slug = $"evidence-{Ids.New():N}"[..20];
        var other = $"other-{Ids.New():N}"[..20];
        var unit = await UnitClientAsync(slug);
        var operatorId = await GrantAsync(SystemRoles.TenantAdministrator);
        using (var anchored = await Send(HttpMethod.Post, "/api/v1/audit-anchors", unit, new { kind = "seal", position = 30, hash = new string('e', 64), at = DateTimeOffset.UtcNow }))
        {
            Assert.Equal(HttpStatusCode.Created, anchored.StatusCode);
        }

        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var chains = scope.ServiceProvider.GetRequiredService<ITenantAuditChains>();
            await chains.RecordVerificationAsync(new AuditVerificationInfo(slug, null, false, 1, 30, new string('e', 64), AuditVerificationResults.Passed, 30, [], null, null, DateTimeOffset.UtcNow), null, Cancel);
            await chains.RecordVerificationAsync(new AuditVerificationInfo(other, null, false, 1, 9, new string('f', 64), AuditVerificationResults.Passed, 9, [], null, null, DateTimeOffset.UtcNow), null, Cancel);
        }

        var period = $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"))}";
        using var mine = await Send(HttpMethod.Get, $"/api/v1/audit-verifications?{period}", unit);
        using var notAUnit = await Send(HttpMethod.Get, $"/api/v1/audit-verifications?{period}", operatorId);
        using var latest = await Send(HttpMethod.Get, "/api/v1/audit-anchors/latest", unit);
        using var verifications = JsonDocument.Parse(await mine.Content.ReadAsStringAsync(Cancel));
        using var anchors = JsonDocument.Parse(await latest.Content.ReadAsStringAsync(Cancel));

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        var only = Assert.Single(verifications.RootElement.GetProperty("verifications").EnumerateArray());
        Assert.Equal((slug, 30L), (only.GetProperty("tenant_slug").GetString(), only.GetProperty("to_position").GetInt64()));
        Assert.NotEqual(Guid.Empty, only.GetProperty("id").GetGuid());
        Assert.Equal(TenancyErrors.AuditAnchorNotAUnit, await ErrorCode(notAUnit));
        await using var check = _app!.Services.CreateAsyncScope();
        var stored = await check.ServiceProvider.GetRequiredService<TenancyDbContext>().AuditAnchors.AsNoTracking().SingleAsync(a => a.TenantSlug == slug && a.Position == 30, Cancel);
        Assert.Equal(stored.Id, anchors.RootElement.GetProperty("seal").GetProperty("id").GetGuid());
    }

    [Fact]
    [Proves("ADR-0050")]
    public async Task TheAppRoleMayOnlyInsertAndReadAnchorsAndVerifications()
    {
        const string role = "tenancy_chain_app";
        await using (var owner = new NpgsqlConnection(fixture.ConnectionString))
        {
            await owner.OpenAsync(Cancel);
            await using var create = new NpgsqlCommand($"do $$ begin if not exists (select 1 from pg_roles where rolname = '{role}') then create role {role} login password 'chain'; end if; end $$", owner);
            await create.ExecuteNonQueryAsync(Cancel);
        }

        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<GrantsRunner>().RunAsync(role, Cancel);
        }

        await using var app = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Username = role, Password = "chain" }.ToString());
        await app.OpenAsync(Cancel);
        foreach (var sql in new[] { "update kernel_tenancy.audit_anchors set hash = hash", "delete from kernel_tenancy.audit_anchors", "update kernel_tenancy.audit_verifications set result = 'passed'", "update kernel_tenancy.audit_restores set reason = reason", "delete from kernel_tenancy.audit_restores" })
        {
            await using var command = new NpgsqlCommand(sql, app);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Cancel))).SqlState);
        }

        await using var read = new NpgsqlCommand("select count(*) from kernel_tenancy.audit_anchors", app);
        await read.ExecuteScalarAsync(Cancel);
    }

    [Fact]
    [Proves("TF-061 AC1", "TF-061 AC2", "ADR-0050")]
    public async Task AVerificationIsKeptAndOneThatFailedRaisesAHighSeverityEvent()
    {
        var slug = $"verify-{Ids.New():N}"[..20];
        var operatorId = await GrantAsync(SystemRoles.TenantAdministrator);
        var eventId = Ids.New();
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var chains = scope.ServiceProvider.GetRequiredService<ITenantAuditChains>();
            await chains.RecordVerificationAsync(new AuditVerificationInfo(slug, null, true, 1, 40, new string('c', 64), AuditVerificationResults.Passed, 40, [], null, null, DateTimeOffset.UtcNow), null, Cancel);
            await chains.RecordVerificationAsync(
                new AuditVerificationInfo(slug, null, false, 41, 45, new string('d', 64), AuditVerificationResults.Failed, 5, [new AuditChainProblemInfo("modified", 43, eventId, $"Event {eventId:D} at position 43 was changed.")], null, null, DateTimeOffset.UtcNow),
                null,
                Cancel);
        }

        using var listed = await Send(HttpMethod.Get, $"/api/v1/tenants/{slug}/audit-verifications?sort=verified_at", operatorId);
        using var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync(Cancel));
        var rows = body.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(["passed", "failed"], rows.Select(r => r.GetProperty("result").GetString()));
        var problem = Assert.Single(rows[1].GetProperty("problems").EnumerateArray());
        Assert.Equal((43, eventId), (problem.GetProperty("position").GetInt64(), problem.GetProperty("event_id").GetGuid()));

        await using var check = _app!.Services.CreateAsyncScope();
        var broken = await check.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .SingleAsync(e => e.EventType == TenancyAuditEvents.AuditChainBroken && e.Target == slug, Cancel);
        Assert.Equal(AuditSeverity.High, broken.Severity);
        Assert.Contains(eventId.ToString("D"), broken.Details, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0050")]
    public async Task ATenantIsDueDailyInFullMonthlyAndAtOnceWhenAnOperatorAsks()
    {
        var slug = $"due-{Ids.New():N}"[..20];
        var operatorId = await GrantAsync(SystemRoles.TenantAdministrator);
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
            Assert.True((await registry.CreateAsync(new NewTenant(slug, "Due Ltd", [], "admin@due.test", null, SizeClass.Small, null, null), Cancel)).IsSuccess);
            Assert.True((await scope.ServiceProvider.GetRequiredService<ITenantRegistry>().SetStatusAsync(slug, TenantStatus.Active, Cancel)).IsSuccess);
        }

        var now = DateTimeOffset.UtcNow;
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var chains = scope.ServiceProvider.GetRequiredService<ITenantAuditChains>();
            var first = Assert.Single(await chains.DueAsync(now, Cancel), d => d.TenantSlug == slug);
            Assert.True(first.Full); // never verified: the whole chain
            await chains.RecordVerificationAsync(new AuditVerificationInfo(slug, null, true, 1, 3, new string('e', 64), AuditVerificationResults.Passed, 3, [], null, null, now), null, Cancel);
            Assert.DoesNotContain(await chains.DueAsync(now.AddHours(23), Cancel), d => d.TenantSlug == slug);
            Assert.False(Assert.Single(await chains.DueAsync(now.AddDays(1).AddMinutes(1), Cancel), d => d.TenantSlug == slug).Full);
            Assert.True(Assert.Single(await chains.DueAsync(now.AddDays(31), Cancel), d => d.TenantSlug == slug).Full);
        }

        using var asked = await Send(HttpMethod.Post, $"/api/v1/tenants/{slug}/audit-verifications", operatorId, new { full = true });
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        using var request = JsonDocument.Parse(await asked.Content.ReadAsStringAsync(Cancel));
        var requestId = request.RootElement.GetProperty("id").GetGuid();
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var chains = scope.ServiceProvider.GetRequiredService<ITenantAuditChains>();
            var due = Assert.Single(await chains.DueAsync(now, Cancel), d => d.TenantSlug == slug);
            Assert.Equal((requestId, true), (due.RequestId, due.Full));
            await chains.RecordVerificationAsync(new AuditVerificationInfo(slug, null, true, 1, 3, new string('e', 64), AuditVerificationResults.Passed, 3, [], null, null, now), requestId, Cancel);
        }

        using var answered = await Send(HttpMethod.Get, $"/api/v1/tenants/{slug}/audit-verifications/requests/{requestId}", operatorId);
        using var answer = JsonDocument.Parse(await answered.Content.ReadAsStringAsync(Cancel));
        Assert.Equal("passed", answer.RootElement.GetProperty("verification").GetProperty("result").GetString());
    }

    [Fact]
    [Proves("TF-061 AC3", "ADR-0050")]
    public async Task AnOperatorAcceptsARestorePointWithATicketAReasonAndAFreshTokenAndTheChainContinuesAsANewSegment()
    {
        var slug = $"restore-{Ids.New():N}"[..20];
        var operatorId = await GrantAsync(SystemRoles.TenantAdministrator);
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
            Assert.True((await registry.CreateAsync(new NewTenant(slug, "Restored Ltd", [], "admin@restored.test", null, SizeClass.Small, null, null), Cancel)).IsSuccess);
            Assert.True((await scope.ServiceProvider.GetRequiredService<ITenantRegistry>().SetStatusAsync(slug, TenantStatus.Active, Cancel)).IsSuccess);
        }

        var unit = await UnitClientAsync(slug);
        var (h2, h4, h7) = (new string('2', 64), new string('4', 64), new string('7', 64));
        foreach (var (position, hash) in new[] { (2L, h2), (4L, h4), (7L, h7) })
        {
            using var sent = await Send(HttpMethod.Post, "/api/v1/audit-anchors", unit, new { kind = "seal", position, hash, at = DateTimeOffset.UtcNow });
            Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        }

        // Nothing restored yet: nothing to accept.
        var acceptance = new { ticket = "INC-77", reason = "Restored from last night's backup after the disk failure" };
        using (var none = await Send(HttpMethod.Post, $"/api/v1/tenants/{slug}/audit-restores", operatorId, acceptance))
        {
            Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
            Assert.Equal(TenancyErrors.AuditRestoreNone, await ErrorCode(none));
        }

        // The fleet reports the restore: the chain matches its anchors up to position 4.
        var restoredAt = DateTimeOffset.UtcNow;
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITenantAuditChains>().RecordVerificationAsync(
                new AuditVerificationInfo(slug, null, true, 1, 6, new string('6', 64), AuditVerificationResults.Restored, 6, [], 4, restoredAt, restoredAt), null, Cancel);
        }

        using (var noTicket = await Send(HttpMethod.Post, $"/api/v1/tenants/{slug}/audit-restores", operatorId, new { reason = "No ticket" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noTicket.StatusCode);
            Assert.Equal(TenancyErrors.AuditRestoreInvalid, await ErrorCode(noTicket));
        }

        using (var stale = await Send(HttpMethod.Post, $"/api/v1/tenants/{slug}/audit-restores", operatorId, acceptance, staleToken: true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
            Assert.Equal(RecentAuthentication.ErrorCode, await ErrorCode(stale));
        }

        using (var accepted = await Send(HttpMethod.Post, $"/api/v1/tenants/{slug}/audit-restores", operatorId, acceptance))
        {
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
            using var body = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(Cancel));
            var restore = body.RootElement.GetProperty("restore");
            Assert.Equal((2, 5L, 4L, 5L, 7L), (restore.GetProperty("segment").GetInt32(), restore.GetProperty("start_position").GetInt64(), restore.GetProperty("restore_position").GetInt64(), restore.GetProperty("lost_from").GetInt64(), restore.GetProperty("lost_to").GetInt64()));
            Assert.Equal(h4, restore.GetProperty("previous_hash").GetString());
            Assert.Equal(("INC-77", acceptance.reason), (restore.GetProperty("ticket").GetString(), restore.GetProperty("reason").GetString()));
            var requestId = body.RootElement.GetProperty("verification_request_id").GetGuid();
            await using var scope = _app!.Services.CreateAsyncScope();
            var due = Assert.Single(await scope.ServiceProvider.GetRequiredService<ITenantAuditChains>().DueAsync(DateTimeOffset.UtcNow, Cancel), d => d.TenantSlug == slug);
            Assert.Equal((requestId, true), (due.RequestId, due.Full));
        }

        using (var twice = await Send(HttpMethod.Post, $"/api/v1/tenants/{slug}/audit-restores", operatorId, acceptance))
        {
            Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        }

        // The unit resumes from the restore point, in segment 2, and hears of the restore to record it in its own trail.
        using (var latest = await Send(HttpMethod.Get, "/api/v1/audit-anchors/latest", unit))
        {
            using var body = JsonDocument.Parse(await latest.Content.ReadAsStringAsync(Cancel));
            Assert.Equal(2, body.RootElement.GetProperty("segment").GetInt32());
            Assert.Equal((4L, h4), (body.RootElement.GetProperty("seal").GetProperty("position").GetInt64(), body.RootElement.GetProperty("seal").GetProperty("hash").GetString()));
            Assert.Equal("INC-77", Assert.Single(body.RootElement.GetProperty("restores").EnumerateArray()).GetProperty("ticket").GetString());
        }

        // A reused position is the new segment's: no conflict with the lost one, which stays in segment 1 as a record.
        var newSeven = new string('a', 64);
        using (var reused = await Send(HttpMethod.Post, "/api/v1/audit-anchors", unit, new { kind = "seal", position = 7, hash = newSeven, at = DateTimeOffset.UtcNow }))
        {
            Assert.Equal(HttpStatusCode.Created, reused.StatusCode);
        }

        using (var before = await Send(HttpMethod.Post, "/api/v1/audit-anchors", unit, new { kind = "seal", position = 4, hash = h4, at = DateTimeOffset.UtcNow }))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var anchors = await scope.ServiceProvider.GetRequiredService<ITenantAuditChains>().AnchorsAsync(slug, Cancel);
            Assert.Equal([(1, h7), (2, newSeven)], anchors.Where(a => a.Position == 7).Select(a => (a.Segment, a.Hash)).OrderBy(a => a.Segment));
            var restores = await scope.ServiceProvider.GetRequiredService<ITenantAuditChains>().RestoresAsync(slug, Cancel);
            Assert.Equal((2, 5L), (Assert.Single(restores).Segment, restores[0].StartPosition));
            var recorded = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
                .SingleAsync(e => e.EventType == TenancyAuditEvents.AuditRestoreAccepted && e.Target == slug, Cancel);
            Assert.Equal((AuditSeverity.High, acceptance.reason), (recorded.Severity, recorded.Reason));
        }

        using var listed = await Send(HttpMethod.Get, $"/api/v1/tenants/{slug}/audit-restores", operatorId);
        using var list = JsonDocument.Parse(await listed.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(7L, Assert.Single(list.RootElement.GetProperty("restores").EnumerateArray()).GetProperty("lost_to").GetInt64());
    }

    /// <summary>A control-plane service account with the unit-client role, bound to the tenant as the fleet binds it.</summary>
    private async Task<Guid> UnitClientAsync(string slug)
    {
        var client = await GrantAsync(SystemRoles.UnitClient);
        await using var scope = _app!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ITenantAuditChains>().BindUnitClientAsync(slug, client, Cancel);
        return client;
    }

    private async Task<Guid> GrantAsync(string role)
    {
        var principal = Ids.New();
        await using var scope = _app!.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, role, Cancel)).IsSuccess);
        return principal;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, Guid principal, object? body = null, bool staleToken = false)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderPrincipal.HeaderName, principal.ToString());
        if (staleToken)
        {
            request.Headers.Add(HeaderRecentAuthentication.HeaderName, "true");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return await _client.SendAsync(request, Cancel);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
        return problem.RootElement.GetProperty("error_code").GetString();
    }
}

/// <summary>Step-up as the tests set it: a token is recent unless the request says it is not, as Identity answers for a real one.</summary>
public sealed class HeaderRecentAuthentication(IHttpContextAccessor accessor) : IRecentAuthentication
{
    public const string HeaderName = "X-Test-Stale-Token";

    public Result Require(TimeSpan maxAge) =>
        accessor.HttpContext?.Request.Headers.ContainsKey(HeaderName) == true ? RecentAuthentication.Required(maxAge) : Result.Success();
}
