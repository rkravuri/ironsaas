using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Flags.Contracts;
using Iron.Kernel.Flags.Services;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Flags.Tests;

/// <summary>
/// The import kill switch through a host with the real modules (TF-051, ADR-0035): role import refused with
/// <c>flags.killed</c>, the operator's reason and a retry time, audited; the rest of the roles API untouched; a tenant's
/// own switch for that tenant only; and in <c>/me/context</c> the flags for everyone and the switches for administrators.
/// </summary>
[Collection(FlagsCollection.Name)]
public class KillSwitchTests(FlagsFixture fixture) : IAsyncLifetime
{
    private static readonly string Imports = KillSwitches.Imports.Key;
    private static readonly object EmptyImport = new { dry_run = true, document = new { format_version = 1, roles = Array.Empty<object>() } };

    private readonly ControlledSource _source = new();
    private readonly TestPrincipals _principals = new();
    private WebApplication? _app;
    private HttpClient _client = null!;
    private Guid _administrator;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
        builder.Services.AddSingleton<IFlagRuleSource>(_source);
        builder.Services.AddSingleton<IPrincipals>(_principals);
        builder.Services.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddScoped<IEntitlementGate, Entitled>();
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, FlagsFixture.Modules());

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
        _administrator = await GrantAsync(_principals.Add(PrincipalKind.User, "Ada"), SystemRoles.TenantAdministrator);
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
    [Proves("ADR-0035")]
    public async Task TheImportKillSwitchStopsRoleImportWithItsOwnCodeTheReasonAndARetryTimeAndIsAudited()
    {
        await PublishAsync(Rules.Set(10, [Rules.Global(Imports, true, reason: "A bad import file is going round; imports wait for the fix.")]));

        using var refused = await SendAsync(HttpMethod.Post, "/api/v1/roles/import", _administrator, EmptyImport);
        using var roles = await SendAsync(HttpMethod.Get, "/api/v1/roles", _administrator);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("30", refused.Headers.RetryAfter?.ToString());
        using (var problem = await JsonAsync(refused))
        {
            Assert.Equal(FlagErrors.Killed, problem.RootElement.GetProperty("error_code").GetString());
            Assert.Equal(Imports, problem.RootElement.GetProperty("flag").GetString());
            Assert.Equal("A bad import file is going round; imports wait for the fix.", problem.RootElement.GetProperty("reason").GetString());
            Assert.Equal("Imports are stopped by the platform operator for now: A bad import file is going round; imports wait for the fix. Try again later.", problem.RootElement.GetProperty("detail").GetString());
        }

        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
        await using var scope = _app!.Services.CreateAsyncScope();
        var denial = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.EventType == FlagsAuditEvents.AccessDenied && e.ActorId == _administrator)
            .SingleAsync(TestContext.Current.CancellationToken);
        var details = JsonSerializer.Deserialize<Dictionary<string, string>>(denial.Details!)!;
        Assert.Equal((FlagErrors.Killed, Imports), (details["error_code"], details["flag"]));

        // Switched off: the import runs again on the next read of the rules.
        await PublishAsync(Rules.Set(11, []));
        using var allowed = await SendAsync(HttpMethod.Post, "/api/v1/roles/import", _administrator, EmptyImport);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    [Proves("ADR-0039")]
    public async Task TheGatesDecisionForAnExplanationIsTheChecksAnswerAndWritesNothing()
    {
        await PublishAsync(Rules.Set(15, [Rules.Global(Imports, true, reason: "Imports wait for a fix.")]));
        var request = new FlagRequest([new FlagRequirement(Imports, MustBeOn: false)], _administrator, "POST /api/v1/roles/import", "/api/v1/roles/import");
        await using var scope = _app!.Services.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<IFlagGate>();
        var events = scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.EventType == FlagsAuditEvents.AccessDenied && e.ActorId == _administrator);
        var before = await events.CountAsync(TestContext.Current.CancellationToken);

        var decided = await gate.DecideAsync(request, TestContext.Current.CancellationToken);
        var afterDeciding = await events.CountAsync(TestContext.Current.CancellationToken);
        var checkedAnswer = await gate.CheckAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(checkedAnswer, decided);
        Assert.Equal((false, FlagErrors.Killed, "Imports wait for a fix."), (decided.Allowed, decided.ErrorCode, decided.Reason));
        Assert.Equal(before, afterDeciding);
        Assert.Equal(before + 1, await events.CountAsync(TestContext.Current.CancellationToken));
        await PublishAsync(Rules.Set(16, []));
    }

    [Fact]
    [Proves("ADR-0035")]
    public async Task ATenantsOwnKillSwitchStopsThatTenantOnly()
    {
        await PublishAsync(Rules.Set(12, [], 12, [Rules.Tenant(Imports, "acme", true, reason: "Acme asked for it.")]));
        using var allowed = await SendAsync(HttpMethod.Post, "/api/v1/roles/import", _administrator, EmptyImport);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        await PublishAsync(Rules.Set(13, [], 13, [Rules.Tenant(Imports, "echo", true, reason: "Echo asked for it.")]));
        using var refused = await SendAsync(HttpMethod.Post, "/api/v1/roles/import", _administrator, EmptyImport);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
    }

    [Fact]
    [Proves("ADR-0035")]
    public async Task TheContextGivesEveryoneTheFlagsAndOnlyAnAdministratorTheKillSwitchesWithTheReason()
    {
        await PublishAsync(Rules.Set(14, [Rules.Global(Imports, true, reason: "Imports wait for a fix."), Rules.Global(ExampleFlags.Tone.Key, "warm")]));
        var reader = await GrantAsync(_principals.Add(PrincipalKind.User, "Grace"), SystemRoles.ReadOnly);

        using var administrator = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/context", _administrator));
        using var other = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/context", reader));

        var flags = administrator.RootElement.GetProperty("flags");
        Assert.True(flags.GetProperty("known").GetBoolean());
        var tone = flags.GetProperty("values").EnumerateArray().Single(v => v.GetProperty("key").GetString() == ExampleFlags.Tone.Key);
        Assert.Equal(("string", "warm", "targeting_match"), (tone.GetProperty("type").GetString(), tone.GetProperty("value").GetString(), tone.GetProperty("reason").GetString()));
        var killed = Assert.Single(flags.GetProperty("kill_switches").EnumerateArray());
        Assert.Equal(("imports", "Imports wait for a fix."), (killed.GetProperty("path").GetString(), killed.GetProperty("reason").GetString()));
        Assert.Equal(Rules.Changed, killed.GetProperty("since").GetDateTimeOffset());

        var readerFlags = other.RootElement.GetProperty("flags");
        Assert.True(readerFlags.GetProperty("values").EnumerateArray().Single(v => v.GetProperty("key").GetString() == Imports).GetProperty("value").GetBoolean());
        Assert.Empty(readerFlags.GetProperty("kill_switches").EnumerateArray());
    }

    private async Task PublishAsync(FlagRuleSet rules)
    {
        _source.Rules = rules;
        Assert.True(await _app!.Services.GetRequiredService<FlagRefresher>().RefreshOnceAsync(TestContext.Current.CancellationToken));
    }

    private async Task<Guid> GrantAsync(Guid principal, string role)
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, role, TestContext.Current.CancellationToken)).IsSuccess);
        return principal;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid principal, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderPrincipal.HeaderName, principal.ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    /// <summary>Every module and feature entitled: this host proves the flags, Iron.Kernel.Entitlements.Tests the entitlements.</summary>
    private sealed class Entitled : IEntitlementGate
    {
        public ValueTask<EntitlementDecision> CheckAsync(EntitlementRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(EntitlementDecision.Allow);
    }
}
