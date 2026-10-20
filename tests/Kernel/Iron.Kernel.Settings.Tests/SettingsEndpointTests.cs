using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Settings.Tests;

/// <summary>
/// The settings API through a host with the real modules (TF-057, TF-058, TF-221; ADR-0036): the rule shown when a value
/// is refused, a scope a setting does not allow, the permission at the place changed (TF-221 AC3), reading refused without
/// the permission (TF-058 AC3), reset answering the inherited value with its source, a person's own preferences without a
/// permission, and the shell's part of <c>/me/context</c> with no secret reference in it.
/// </summary>
[Collection(SettingsCollection.Name)]
public class SettingsEndpointTests(SettingsFixture fixture) : IAsyncLifetime
{
    private readonly TestPrincipals _principals = new();
    private WebApplication? _app;
    private HttpClient _client = null!;
    private Guid _administrator;
    private Guid _branch;
    private Guid _desk;
    private Guid _otherBranch;
    private Guid _branchAdministrator;
    private Guid _clerk;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
        builder.Services.AddSingleton<IPrincipals>(_principals);
        builder.Services.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, SettingsFixture.Modules());

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();

        _branch = await SettingsFixture.UnitAsync(_app.Services, "Branch");
        _desk = await SettingsFixture.UnitAsync(_app.Services, "Desk", _branch);
        _otherBranch = await SettingsFixture.UnitAsync(_app.Services, "Other branch");
        _administrator = await GrantAsync(_principals.Add("Ada"), SystemRoles.TenantAdministrator, RoleScope.Tenant);
        _branchAdministrator = await GrantAsync(_principals.Add("Ravi", _branch), SystemRoles.TenantAdministrator, RoleScope.Unit(_branch, includeChildren: true));
        _clerk = _principals.Add("Clerk", _desk);
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
    public async Task AValueThatBreaksTheRuleIsRefusedWithTheRuleOnTheFieldAndAScopeNotAllowedIsRefused()
    {
        using var refused = await JsonAsync(await SendAsync(HttpMethod.Put, $"/api/v1/settings/{ProbeSettings.Batch.Key}", _administrator, new { scope = "tenant", value = 5 }), HttpStatusCode.BadRequest);
        Assert.Equal("settings.value.invalid", refused.RootElement.GetProperty("error_code").GetString());
        var field = refused.RootElement.GetProperty("errors").EnumerateArray().Single();
        Assert.Equal(("value", "A whole number from 10 to 500."), (field.GetProperty("field").GetString(), field.GetProperty("message").GetString()));

        using var scope = await JsonAsync(await SendAsync(HttpMethod.Put, $"/api/v1/settings/{ProbeSettings.MailPassword.Key}", _administrator, new { scope = "unit", unit_id = _branch, value = "tenants/echo/settings/mail" }), HttpStatusCode.BadRequest);
        Assert.Equal("settings.scope.not_allowed", scope.RootElement.GetProperty("error_code").GetString());
        Assert.Equal("The setting 'probe.mail.password' cannot be set for an organisation unit. It may be set for the whole organisation.", scope.RootElement.GetProperty("detail").GetString());

        using var unknown = await JsonAsync(await SendAsync(HttpMethod.Put, "/api/v1/settings/probe.not_declared", _administrator, new { scope = "tenant", value = 1 }), HttpStatusCode.NotFound);
        Assert.Equal("settings.setting.unknown", unknown.RootElement.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task AUnitsAdministratorChangesTheirUnitAndTheUnitsBelowButNotTheTenantNorAnotherUnit()
    {
        using (var below = await SendAsync(HttpMethod.Put, $"/api/v1/settings/{ProbeSettings.Batch.Key}", _branchAdministrator, new { scope = "unit", unit_id = _desk, value = 80 }))
        {
            Assert.Equal(HttpStatusCode.OK, below.StatusCode);
        }

        using var tenant = await JsonAsync(await SendAsync(HttpMethod.Put, $"/api/v1/settings/{ProbeSettings.Batch.Key}", _branchAdministrator, new { scope = "tenant", value = 80 }), HttpStatusCode.Forbidden);
        Assert.Equal(("authz.permission_denied", "settings.values.manage"), (tenant.RootElement.GetProperty("error_code").GetString(), tenant.RootElement.GetProperty("permission").GetString()));
        using var other = await JsonAsync(await SendAsync(HttpMethod.Put, $"/api/v1/settings/{ProbeSettings.Batch.Key}", _branchAdministrator, new { scope = "unit", unit_id = _otherBranch, value = 80 }), HttpStatusCode.Forbidden);
        Assert.Equal("You need settings.values.manage at this unit or a unit above it for this. Ask an administrator who holds it.", other.RootElement.GetProperty("detail").GetString());

        // Reading needs the permission at the place read too, and someone without it at all is refused (TF-058 AC3).
        using (var ownTree = await SendAsync(HttpMethod.Get, $"/api/v1/settings?scope=unit&unit={_desk}", _branchAdministrator))
        {
            Assert.Equal(HttpStatusCode.OK, ownTree.StatusCode);
        }

        using (var tenantRead = await SendAsync(HttpMethod.Get, "/api/v1/settings?scope=tenant", _branchAdministrator))
        {
            Assert.Equal(HttpStatusCode.Forbidden, tenantRead.StatusCode);
        }

        using var clerk = await SendAsync(HttpMethod.Get, "/api/v1/settings?scope=tenant", _clerk);
        Assert.Equal(HttpStatusCode.Forbidden, clerk.StatusCode);
        Assert.True((await SendAsync(HttpMethod.Delete, $"/api/v1/settings/{ProbeSettings.Batch.Key}?scope=unit&unit={_desk}", _administrator)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task ResetRemovesTheOverrideAndAnswersTheInheritedValueWithWhereItComesFromAndTheHistoryShowsBoth()
    {
        Assert.True((await SendAsync(HttpMethod.Put, $"/api/v1/settings/{ProbeSettings.Tone.Key}", _administrator, new { scope = "unit", unit_id = _branch, value = "brief" })).IsSuccessStatusCode);
        using var set = await JsonAsync(await SendAsync(HttpMethod.Put, $"/api/v1/settings/{ProbeSettings.Tone.Key}", _administrator, new { scope = "unit", unit_id = _desk, value = "warm" }), HttpStatusCode.OK);
        Assert.Equal(("warm", true), (set.RootElement.GetProperty("value").GetString(), set.RootElement.GetProperty("set_here").GetBoolean()));

        using var reset = await JsonAsync(await SendAsync(HttpMethod.Delete, $"/api/v1/settings/{ProbeSettings.Tone.Key}?scope=unit&unit={_desk}", _administrator), HttpStatusCode.OK);
        Assert.Equal(("brief", false), (reset.RootElement.GetProperty("value").GetString(), reset.RootElement.GetProperty("set_here").GetBoolean()));
        var source = reset.RootElement.GetProperty("source");
        Assert.Equal("unit", source.GetProperty("level").GetString());
        Assert.EndsWith(" / Branch", source.GetProperty("unit").GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.Equal("Ada", source.GetProperty("changed_by").GetProperty("name").GetString());

        using var list = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/settings?scope=unit&unit={_desk}", _administrator), HttpStatusCode.OK);
        var keys = list.RootElement.GetProperty("data").EnumerateArray().Select(s => s.GetProperty("key").GetString()).ToList();
        // At a unit: what a unit may hold, never a tenant-only or a person-only setting.
        Assert.Contains(ProbeSettings.Tone.Key, keys);
        Assert.DoesNotContain(ProbeSettings.MailPassword.Key, keys);
        Assert.DoesNotContain("shell.density", keys);

        using var history = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/settings/{ProbeSettings.Tone.Key}/history?scope=unit&unit={_desk}", _administrator), HttpStatusCode.OK);
        var changes = history.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(["reset", "set"], changes.Select(c => c.GetProperty("kind").GetString()));
        Assert.Equal(("warm", JsonValueKind.Null, "Ada"), (changes[0].GetProperty("before").GetString(), changes[0].GetProperty("after").ValueKind, changes[0].GetProperty("changed_by_name").GetString()));
        Assert.True((await SendAsync(HttpMethod.Delete, $"/api/v1/settings/{ProbeSettings.Tone.Key}?scope=unit&unit={_branch}", _administrator)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task APersonSetsTheirOwnPreferenceWithoutAPermissionAndTheShellSeesNoSecretReference()
    {
        using var saved = await JsonAsync(await SendAsync(HttpMethod.Put, "/api/v1/settings/shell.density", _clerk, new { scope = "user", value = "compact" }), HttpStatusCode.OK);
        Assert.Equal(("compact", "user"), (saved.RootElement.GetProperty("value").GetString(), saved.RootElement.GetProperty("source").GetProperty("level").GetString()));
        Assert.True((await SendAsync(HttpMethod.Put, $"/api/v1/settings/{ProbeSettings.MailPassword.Key}", _administrator, new { scope = "tenant", value = "tenants/echo/settings/mail" })).IsSuccessStatusCode);

        using var context = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/context", _clerk), HttpStatusCode.OK);
        var values = context.RootElement.GetProperty("settings").GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("key").GetString()!, v => v);
        Assert.Equal(("compact", "user"), (values["shell.density"].GetProperty("value").GetString(), values["shell.density"].GetProperty("level").GetString()));
        Assert.Equal(("auto", "default"), (values["shell.colour_scheme"].GetProperty("value").GetString(), values["shell.colour_scheme"].GetProperty("level").GetString()));
        Assert.DoesNotContain(ProbeSettings.MailPassword.Key, values.Keys);
        Assert.DoesNotContain(ProbeSettings.Batch.Key, values.Keys);

        // The console shows the reference, a path, and nothing else.
        using var list = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/settings?scope=tenant", _administrator), HttpStatusCode.OK);
        var secret = list.RootElement.GetProperty("data").EnumerateArray().Single(s => s.GetProperty("key").GetString() == ProbeSettings.MailPassword.Key);
        Assert.Equal(("secret_reference", "tenants/echo/settings/mail"), (secret.GetProperty("type").GetString(), secret.GetProperty("value").GetString()));

        // Nobody sets someone else's preference: the user scope is always the caller's.
        using var others = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/context", _administrator), HttpStatusCode.OK);
        Assert.Equal("comfortable", others.RootElement.GetProperty("settings").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("key").GetString() == "shell.density").GetProperty("value").GetString());
        Assert.True((await SendAsync(HttpMethod.Delete, $"/api/v1/settings/{ProbeSettings.MailPassword.Key}?scope=tenant", _administrator)).IsSuccessStatusCode);
        Assert.True((await SendAsync(HttpMethod.Delete, "/api/v1/settings/shell.density?scope=user", _clerk)).IsSuccessStatusCode);
    }

    private async Task<Guid> GrantAsync(Guid principal, string role, RoleScope scope)
    {
        await using var services = _app!.Services.CreateAsyncScope();
        var granted = await services.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, role, scope, TestContext.Current.CancellationToken);
        Assert.True(granted.IsSuccess, granted.IsFailure ? granted.Error.Message : null);
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

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
            return JsonDocument.Parse(text);
        }
    }

    /// <summary>Every module and feature entitled and no kill switch on: this host proves the settings.</summary>
    private sealed class AllEntitled : IEntitlementGate, IFlagGate
    {
        public ValueTask<EntitlementDecision> CheckAsync(EntitlementRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(EntitlementDecision.Allow);

        public ValueTask<FlagDecision> CheckAsync(FlagRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(FlagDecision.Allow);
    }
}
