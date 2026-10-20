using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Fleet;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Telemetry;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Tests;

/// <summary>
/// Plans and tenants' entitlements in the control plane over HTTP (TF-040, TF-042, TF-045): the seeded plans, a plan
/// previewed then applied, an unknown or platform code refused, the history in order with operator, before and after,
/// and no way to edit it.
/// </summary>
[Collection(TenancyCollection.Name)]
public class PlansEndpointTests(TenancyFixture fixture) : IAsyncLifetime
{
    private const string RoleTransfer = "authorization.role_transfer";

    private WebApplication? _app;
    private HttpClient _client = null!;
    private Guid _operator;

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
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new AuthorizationModule(), new FleetModule(), new TenancyModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();
        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
        _operator = await GrantAsync(SystemRoles.TenantAdministrator);
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
    [Proves("ADR-0034")]
    public async Task TheSeedHasStandardWithoutRoleTransferAndEnterpriseWithIt()
    {
        using var standard = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/plans/standard"));
        using var enterprise = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/plans/Enterprise"));
        using var list = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/plans"));

        Assert.Empty(standard.RootElement.GetProperty("features").EnumerateArray());
        Assert.Equal([RoleTransfer], enterprise.RootElement.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(30, enterprise.RootElement.GetProperty("grace_days").GetInt32());
        Assert.Contains(list.RootElement.GetProperty("data").EnumerateArray(), p => p.GetProperty("code").GetString() == "standard");
    }

    [Fact]
    [Proves("TF-042 AC1", "ADR-0034")]
    public async Task APlanIsPreviewedWithoutAChangeThenAppliedInOneTransactionAndAudited()
    {
        var slug = await CreateTenantAsync();
        using var start = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{slug}/entitlements"));
        Assert.Equal(("standard", 1), (start.RootElement.GetProperty("plan").GetString(), start.RootElement.GetProperty("revision").GetInt32()));

        using var preview = await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{slug}/plan", new { plan = "Enterprise", dry_run = true }));
        var root = preview.RootElement;
        Assert.False(root.GetProperty("applied").GetBoolean());
        Assert.Equal(("standard", "enterprise"), (root.GetProperty("plan_before").GetString(), root.GetProperty("plan_after").GetString()));
        var added = Assert.Single(root.GetProperty("added").EnumerateArray());
        Assert.Equal(("feature", RoleTransfer, "enabled"), (added.GetProperty("kind").GetString(), added.GetProperty("code").GetString(), added.GetProperty("after").GetProperty("status").GetString()));
        Assert.Empty(root.GetProperty("removed").EnumerateArray());
        using (var unchanged = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{slug}/entitlements")))
        {
            Assert.Equal(1, unchanged.RootElement.GetProperty("revision").GetInt32());
            Assert.Empty(unchanged.RootElement.GetProperty("entitlements").EnumerateArray());
        }

        using var applied = await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{slug}/plan", new { plan = "enterprise", dry_run = false, ends_on = "2027-06-30" }));
        Assert.True(applied.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal(2, applied.RootElement.GetProperty("revision").GetInt32());
        using var now = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{slug}/entitlements"));
        var feature = Assert.Single(now.RootElement.GetProperty("entitlements").EnumerateArray());
        Assert.Equal((RoleTransfer, "2027-06-30"), (feature.GetProperty("code").GetString(), feature.GetProperty("ends_on").GetString()));
        Assert.Equal(("enterprise", 30), (now.RootElement.GetProperty("plan").GetString(), now.RootElement.GetProperty("grace_days").GetInt32()));
        // No store in this host: the store's copy stays behind until the fleet's reconcile writes it.
        Assert.Equal(0, now.RootElement.GetProperty("published_revision").GetInt32());

        await using var scope = _app!.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.EventType == TenancyAuditEvents.PlanApplied && e.Target == slug && e.ActorId == _operator)
            .ToListAsync(TestContext.Current.CancellationToken);
        // Two: the tenant's first plan when it was created, and this one.
        Assert.Equal(2, audit.Count);
        var details = audit.Select(e => JsonSerializer.Deserialize<Dictionary<string, string>>(e.Details!)!).ToList();
        var enterprise = Assert.Single(details, d => d["plan"] == "enterprise");
        Assert.Equal(("standard", "2", RoleTransfer), (enterprise["plan_before"], enterprise["revision"], enterprise["added"]));

        // The same plan again changes nothing and writes nothing.
        using var again = await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{slug}/plan", new { plan = "enterprise", dry_run = false, ends_on = "2027-06-30" }));
        Assert.Equal(2, again.RootElement.GetProperty("revision").GetInt32());
    }

    [Fact]
    [Proves("TF-042 AC3", "ADR-0034")]
    public async Task APlanWithAnUnknownFeatureOrAPlatformModuleIsRefusedWhenSavedAndWhenApplied()
    {
        using var unknown = await SendAsync(HttpMethod.Put, "/api/v1/plans/premium", new { name = "Premium", grace_days = 7, features = new[] { RoleTransfer, "authorization.mind_reading" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using (var problem = await JsonAsync(unknown))
        {
            Assert.Equal(TenancyErrors.PlanUnknownEntitlements, problem.RootElement.GetProperty("error_code").GetString());
            Assert.Contains("'authorization.mind_reading' is not a feature of this build", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }

        using var platform = await SendAsync(HttpMethod.Put, "/api/v1/plans/premium", new { name = "Premium", grace_days = 7, modules = new[] { new { code = "identity", status = "enabled" } } });
        using (var problem = await JsonAsync(platform))
        {
            Assert.Equal(TenancyErrors.PlanUnknownEntitlements, problem.RootElement.GetProperty("error_code").GetString());
            Assert.Contains("'identity' is a platform module: it is always on and is never in a plan", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }

        using var saved = await SendAsync(HttpMethod.Put, "/api/v1/plans/premium", new { name = "Premium", grace_days = 7, features = new[] { RoleTransfer } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // A plan an older build saved with a feature this build dropped is refused when it is applied (TF-042 AC3).
        var code = $"legacy-{Ids.New():N}"[..16];
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            context.Plans.Add(new Plan(code, "Legacy", 7, [], [RoleTransfer, "legacy.retired_feature"]));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var slug = await CreateTenantAsync();
        using var refused = await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{slug}/plan", new { plan = code, dry_run = false });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(TenancyErrors.PlanUnknownEntitlements, await ErrorCodeAsync(refused));
        using var after = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{slug}/entitlements"));
        Assert.Equal("standard", after.RootElement.GetProperty("plan").GetString());

        using var missing = await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{slug}/plan", new { plan = "platinum", dry_run = true });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var badDates = await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{slug}/plan", new { plan = "enterprise", dry_run = true, starts_on = "2027-01-01", ends_on = "2026-12-31" });
        Assert.Equal(TenancyErrors.PlanDatesInvalid, await ErrorCodeAsync(badDates));
    }

    [Fact]
    [Proves("TF-045 AC1", "TF-045 AC2", "TF-045 AC3", "ADR-0034")]
    public async Task TheHistoryListsEveryChangeInOrderWithOperatorBeforeAndAfterAndCannotBeEdited()
    {
        var slug = await CreateTenantAsync();
        await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{slug}/plan", new { plan = "enterprise", dry_run = false });
        await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{slug}/plan", new { plan = "standard", dry_run = false });

        using var history = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{slug}/entitlements/history"));
        var rows = history.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal([1, 2, 2, 2, 3, 3, 3], rows.Select(r => r.GetProperty("revision").GetInt32()));
        Assert.All(rows, r => Assert.Equal(_operator, r.GetProperty("operator_id").GetGuid()));
        var added = rows.Single(r => r.GetProperty("revision").GetInt32() == 2 && r.GetProperty("code").GetString() == RoleTransfer);
        Assert.Equal(JsonValueKind.Null, added.GetProperty("before").ValueKind);
        Assert.Equal("enabled", added.GetProperty("after").GetProperty("status").GetString());
        var removed = rows.Single(r => r.GetProperty("revision").GetInt32() == 3 && r.GetProperty("code").GetString() == RoleTransfer);
        Assert.Equal("enabled", removed.GetProperty("before").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("after").ValueKind);
        var plan = rows.Single(r => r.GetProperty("revision").GetInt32() == 3 && r.GetProperty("kind").GetString() == "plan");
        Assert.Equal(("enterprise", "standard"), (plan.GetProperty("before").GetProperty("code").GetString(), plan.GetProperty("after").GetProperty("code").GetString()));
        Assert.Equal("Plan standard applied", plan.GetProperty("reason").GetString());

        // TF-045 AC3: no route writes the history, the save interceptor refuses a change, and the app role may only insert and read.
        var id = rows[0].GetProperty("id").GetGuid();
        using var put = await SendAsync(HttpMethod.Put, $"/api/v1/tenants/{slug}/entitlements/history/{id}", new { reason = "edited" });
        using var delete = await SendAsync(HttpMethod.Delete, $"/api/v1/tenants/{slug}/entitlements/history/{id}");
        Assert.True(put.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, put.StatusCode.ToString());
        Assert.True(delete.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, delete.StatusCode.ToString());

        await using var scope = _app!.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var change = await context.EntitlementHistory.SingleAsync(c => c.Id == id, TestContext.Current.CancellationToken);
        context.Entry(change).Property(c => c.Reason).CurrentValue = "edited";
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
        context.ChangeTracker.Clear();
        context.EntitlementHistory.Remove(await context.EntitlementHistory.SingleAsync(c => c.Id == id, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        var grants = GrantsRunner.Statements("control", "control_app", [("kernel_tenancy", context.Model)]);
        Assert.Contains("GRANT SELECT, INSERT ON \"kernel_tenancy\".\"entitlement_history\" TO \"control_app\"", grants);
        Assert.Contains("GRANT SELECT, INSERT, UPDATE, DELETE ON \"kernel_tenancy\".\"tenant_entitlements\" TO \"control_app\"", grants);
    }

    [Fact]
    [Proves("ADR-0034")]
    public async Task ATenantStartsWithItsPlanAndAnUnknownPlanRefusesIt()
    {
        var slug = $"plan-{Ids.New():N}"[..16];
        using var refused = await SendAsync(HttpMethod.Post, "/api/v1/tenants", new { slug, display_name = "Plan test", administrator_login_id = $"admin@{slug}.test", plan = "platinum" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(TenancyErrors.PlanNotFound, await ErrorCodeAsync(refused));
        using var gone = await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        using var created = await SendAsync(HttpMethod.Post, "/api/v1/tenants", new { slug, display_name = "Plan test", administrator_login_id = $"admin@{slug}.test", plan = "Enterprise" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var entitlements = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{slug}/entitlements"));
        Assert.Equal(("enterprise", 1), (entitlements.RootElement.GetProperty("plan").GetString(), entitlements.RootElement.GetProperty("revision").GetInt32()));
        Assert.Equal(RoleTransfer, Assert.Single(entitlements.RootElement.GetProperty("entitlements").EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    [Proves("TF-180 AC1")]
    public async Task AReaderSeesPlansAndHistoryButCannotSaveOrApply()
    {
        var reader = await GrantAsync(SystemRoles.ReadOnly);
        var slug = await CreateTenantAsync();

        using var list = await SendAsync(HttpMethod.Get, "/api/v1/plans", reader);
        using var history = await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{slug}/entitlements/history", reader);
        using var save = await SendAsync(HttpMethod.Put, "/api/v1/plans/standard", new { name = "Standard", grace_days = 1 }, reader);
        using var apply = await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{slug}/plan", new { plan = "enterprise", dry_run = true }, reader);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, apply.StatusCode);
    }

    private async Task<string> CreateTenantAsync()
    {
        var slug = $"ent-{Ids.New():N}"[..16];
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/tenants", new { slug, display_name = "Entitled", administrator_login_id = $"admin@{slug}.test" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return slug;
    }

    private async Task<Guid> GrantAsync(string role)
    {
        var principal = Ids.New();
        await using var scope = _app!.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, role, TestContext.Current.CancellationToken)).IsSuccess);
        return principal;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, Guid? principal = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderPrincipal.HeaderName, (principal ?? _operator).ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var problem = await JsonAsync(response);
        return problem.RootElement.GetProperty("error_code").GetString();
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
}
