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
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data;
using Iron.Kernel.Events;
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
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Tenancy.Tests;

/// <summary>
/// Flag rules in the control plane over HTTP (TF-050 to TF-053, ADR-0035): rules replaced with their revision, refused for
/// an unknown flag, a wrong type or a target that does not exist, audited with before and after; a kill switch flipped
/// only by an operator, with a reason; the paths a change touches marked behind; and the report.
/// </summary>
[Collection(TenancyCollection.Name)]
public class FlagRulesEndpointTests(TenancyFixture fixture) : IAsyncLifetime
{
    private const string Release = "flags.example_release";
    private const string Imports = "kill.imports";

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
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new FleetModule(), new TenancyModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();
        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
        _operator = await GrantAsync(Ids.New(), SystemRoles.TenantAdministrator);
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
    [Proves("TF-053 AC1", "ADR-0035")]
    public async Task RulesAreReplacedWithTheirRevisionAuditedWithBeforeAndAfterAndTheirPathsMarkedBehind()
    {
        var slug = await CreateTenantAsync();
        using var first = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/flags/{Release}/rules"));
        var revision = first.RootElement.GetProperty("revision").GetInt32();

        var rules = new object[]
        {
            new { scope = "tenant", tenant = slug, value = true },
            new { scope = "global", value = false, condition = new { kind = "percentage", percentage = 10, of = "tenant" } },
            new { scope = "global", value = true, condition = new { kind = "attribute", attribute = "plan", values = new[] { "enterprise" } } },
        };
        using var replaced = await SendAsync(HttpMethod.Put, $"/api/v1/flags/{Release}/rules", new { revision, rules });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        using (var body = await JsonAsync(replaced))
        {
            Assert.Equal(revision + 1, body.RootElement.GetProperty("revision").GetInt32());
            var saved = body.RootElement.GetProperty("rules").EnumerateArray().ToList();
            Assert.Equal(["tenant", "global", "global"], saved.Select(r => r.GetProperty("scope").GetString()));
            Assert.Equal(10, saved[1].GetProperty("condition").GetProperty("percentage").GetInt32());
        }

        // The same revision again: someone changed the rules in between.
        using var stale = await SendAsync(HttpMethod.Put, $"/api/v1/flags/{Release}/rules", new { revision, rules = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(TenancyErrors.FlagRulesChanged, await ErrorCodeAsync(stale));

        using var history = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/flags/{Release}/history?sort=-revision&page_size=1"));
        var change = Assert.Single(history.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal((revision + 1, "rules", _operator), (change.GetProperty("revision").GetInt32(), change.GetProperty("kind").GetString(), change.GetProperty("operator_id").GetGuid()));
        Assert.Equal(3, change.GetProperty("after").GetArrayLength());

        await using var scope = _app!.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.EventType == TenancyAuditEvents.FlagRulesChanged && e.ActorId == _operator).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(audit);
        // No store in this host: both paths the change touched wait for the fleet's reconcile.
        var publications = await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().FlagPublications.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(publications, p => p.Path == "platform/flags" && p.PublishedRevision < p.Revision);
        Assert.Contains(publications, p => p.Path == $"tenants/{slug}/flags" && p.PublishedRevision < p.Revision);
    }

    [Fact]
    [Proves("ADR-0035")]
    public async Task ARuleForAnUnknownFlagAValueOfAnotherTypeOrATargetThatDoesNotExistIsRefused()
    {
        using var unknown = await SendAsync(HttpMethod.Put, "/api/v1/flags/flags.not_declared/rules", new { rules = new[] { new { scope = "global", value = true } } });
        using var wrongType = await SendAsync(HttpMethod.Put, $"/api/v1/flags/{Release}/rules", new { rules = new[] { new { scope = "global", value = (object)"yes" } } });
        using var noTenant = await SendAsync(HttpMethod.Put, $"/api/v1/flags/{Release}/rules", new { rules = new[] { new { scope = "tenant", tenant = "nobody-here", value = (object)true } } });
        using var badCondition = await SendAsync(HttpMethod.Put, $"/api/v1/flags/{Release}/rules", new { rules = new[] { new { scope = "global", value = (object)true, condition = new { kind = "attribute", attribute = "shoe_size", values = new[] { "9" } } } } });

        Assert.Equal((HttpStatusCode.BadRequest, TenancyErrors.FlagUnknown), (unknown.StatusCode, await ErrorCodeAsync(unknown)));
        Assert.Equal(TenancyErrors.FlagValueType, await ErrorCodeAsync(wrongType));
        Assert.Equal(TenancyErrors.FlagRuleInvalid, await ErrorCodeAsync(noTenant));
        Assert.Equal(TenancyErrors.FlagRuleInvalid, await ErrorCodeAsync(badCondition));
    }

    [Fact]
    [Proves("TF-051 AC3", "TF-051 AC4", "ADR-0035")]
    public async Task OnlyAnOperatorFlipsAKillSwitchAlwaysWithAReasonAndTheFlipIsInTheHistory()
    {
        var reader = await GrantAsync(Ids.New(), SystemRoles.ReadOnly);
        var flagEditor = await GrantAsync(Ids.New(), await FlagEditorRoleAsync());

        using var byReader = await SendAsync(HttpMethod.Put, "/api/v1/kill-switches/imports", new { on = true, reason = "Trying." }, reader);
        using var byEditor = await SendAsync(HttpMethod.Put, $"/api/v1/flags/{Imports}/rules", new { reason = "Trying.", rules = new[] { new { scope = "global", value = true } } }, flagEditor);
        using var noReason = await SendAsync(HttpMethod.Put, "/api/v1/kill-switches/imports", new { on = true, reason = "" });
        Assert.Equal(HttpStatusCode.Forbidden, byReader.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byEditor.StatusCode);
        Assert.Equal("tenancy.kill_switches.manage", (await JsonAsync(byEditor)).RootElement.GetProperty("permission").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        using var on = await SendAsync(HttpMethod.Put, "/api/v1/kill-switches/imports", new { on = true, reason = "A bad import file is going round." });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        using (var states = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/kill-switches")))
        {
            var imports = states.RootElement.GetProperty("data").EnumerateArray().Single(s => s.GetProperty("path").GetString() == "imports");
            Assert.True(imports.GetProperty("on").GetBoolean());
            Assert.Equal("A bad import file is going round.", imports.GetProperty("reason").GetString());
            Assert.Equal(6, states.RootElement.GetProperty("data").GetArrayLength());
        }

        using var off = await SendAsync(HttpMethod.Put, "/api/v1/kill-switches/imports", new { on = false, reason = "The fix is out." });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        using var history = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/flags/{Imports}/history?sort=-revision&page_size=2"));
        var flips = history.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.All(flips, f => Assert.Equal(("kill_switch", _operator), (f.GetProperty("kind").GetString(), f.GetProperty("operator_id").GetGuid())));
        Assert.Equal(["The fix is out.", "A bad import file is going round."], flips.Select(f => f.GetProperty("reason").GetString()));
        Assert.Equal(0, flips[0].GetProperty("after").GetArrayLength());

        await using var scope = _app!.Services.CreateAsyncScope();
        var flipped = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.EventType == TenancyAuditEvents.KillSwitchFlipped && e.ActorId == _operator).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["A bad import file is going round.", "The fix is out."], flipped.OrderBy(e => e.CreatedAt).Select(e => e.Reason));
    }

    [Fact]
    [Proves("ADR-0035")]
    public async Task AKillSwitchRuleThatSaysFalseIsRefusedWithItsOwnCodeSinceRemovingTheRuleTurnsTheSwitchOff()
    {
        var tenant = await CreateTenantAsync();

        // An exemption of one tenant from a switch that is on for the platform: refused, and nothing is written.
        using var refused = await SendAsync(HttpMethod.Put, $"/api/v1/flags/{Imports}/rules", new { reason = "Exempt one tenant.", rules = new object[] { new { scope = "global", value = true }, new { scope = "tenant", tenant, value = false } } });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(TenancyErrors.KillSwitchFalseRule, await ErrorCodeAsync(refused));
        using var rules = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/flags/{Imports}/rules"));
        Assert.DoesNotContain(rules.RootElement.GetProperty("rules").EnumerateArray(), rule => rule.GetProperty("value").ValueKind == JsonValueKind.False);
    }

    [Fact]
    [Proves("TF-052 AC1", "ADR-0035")]
    public async Task TheReportNamesFlagsPastTheirRemovalDateWithTheirOwnerAndRulesLeftForFlagsThatAreGone()
    {
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            // A rule a retired flag left behind, as an older build would have it.
            var entry = new Iron.Kernel.Flags.Contracts.FlagRuleEntry(Guid.Empty, "flags.long_gone", Iron.Kernel.Flags.Contracts.FlagScope.Global, null, null, null, null, Iron.Kernel.Flags.Contracts.FlagCondition.Always, JsonSerializer.SerializeToElement(true), 0, null, default);
            context.FlagRules.Add(new FlagRule("flags.long_gone", entry, 0));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var report = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/flags/report"));
        Assert.Contains(report.RootElement.GetProperty("orphans").EnumerateArray(), o => o.GetProperty("flag").GetString() == "flags.long_gone" && o.GetProperty("why").GetString() == "not in the catalogue");

        // On a later day the example flags are overdue, each with its owner.
        await using var provider = fixture.BuildServices(new FakeTimeProvider(new DateTimeOffset(2027, 4, 1, 0, 0, 0, TimeSpan.Zero)), new FixedPrincipal(SystemPrincipal.SystemId));
        await using var later = provider.CreateAsyncScope();
        var overdue = (await later.ServiceProvider.GetRequiredService<FlagReportService>().BuildAsync(TestContext.Current.CancellationToken)).PastRemoval;
        Assert.Equal([("flags.example_release", "Platform team"), ("flags.example_tone", "Platform team")], overdue.Select(f => (f.Key, f.Owner)).Order());
    }

    private async Task<string> FlagEditorRoleAsync()
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var code = $"flag-editor-{Ids.New():N}"[..20];
        context.Roles.Add(new Role(code, "Flag editor", "Sets flag rules, never kill switches.", false, PrincipalKind.Any, ["tenancy.flags.read", "tenancy.flags.manage"]));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return code;
    }

    private async Task<string> CreateTenantAsync()
    {
        var slug = $"flag-{Ids.New():N}"[..16];
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/tenants", new { slug, display_name = "Flagged", administrator_login_id = $"admin@{slug}.test" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return slug;
    }

    private async Task<Guid> GrantAsync(Guid principal, string role)
    {
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
