using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Settings;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// Acting for a delegator over HTTP (TF-037, ADR-0037): chosen explicitly, with exactly the delegator's rights for the
/// delegated duties, both people on every audit event and in the audit list, cleared and announced when the delegation
/// ends, and never for a token.
/// </summary>
[Collection(IdentityCollection.Name)]
public class ActingForTests(IdentityFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";

    private readonly FakeTimeProvider _time = new();
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(IdentityFixture.Configuration);
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddSingleton(IdentityFixture.Tenant);
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new AuthorizationModule(), new IdentityModule(), new SettingsModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.UseIronIdentity();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
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
    [Proves("TF-037 AC3", "ADR-0037")]
    public async Task ADelegateActsForTheAdministratorWithTheDelegatedDutyOnlyAndEveryEventNamesBoth()
    {
        var (administrator, administratorCookie) = await PersonAsync("admin", SystemRoles.TenantAdministrator);
        var (grace, graceCookie) = await PersonAsync("grace", null);

        using (var created = await SendAsync(HttpMethod.Post, "/api/v1/delegations", administratorCookie, new
        {
            delegate_id = grace,
            duties = new[] { "identity.user_administration" },
            starts_at = _time.GetUtcNow().AddMinutes(-1),
            ends_at = _time.GetUtcNow().AddMinutes(30),
            reason = "Covering the user desk.",
        }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        // As herself, Grace may not list users; /me shows the delegation she holds and no acting.
        using (var denied = await SendAsync(HttpMethod.Get, "/api/v1/users", graceCookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var me = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me", graceCookie)))
        {
            Assert.Equal(JsonValueKind.Null, me.RootElement.GetProperty("acting_for").ValueKind);
            var held = Assert.Single(me.RootElement.GetProperty("delegations").GetProperty("received").EnumerateArray());
            Assert.Equal((administrator, "user administration"), (held.GetProperty("delegator_id").GetGuid(), held.GetProperty("duties")[0].GetProperty("name").GetString()));
        }

        // She chooses to act for the administrator: the administrator's rights for user administration, nothing more.
        using (var acting = await JsonAsync(await SendAsync(HttpMethod.Post, "/api/v1/me/acting-for", graceCookie, new { principal_id = administrator })))
        {
            Assert.Equal(administrator, acting.RootElement.GetProperty("acting_for").GetProperty("id").GetGuid());
            var permissions = acting.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
            Assert.Contains(IdentityPermissions.UsersRead.Name, permissions);
            Assert.DoesNotContain(AuthorizationPermissions.RolesManage.Name, permissions);
        }

        using (var users = await SendAsync(HttpMethod.Get, "/api/v1/users", graceCookie))
        {
            Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        }

        // Creating a role is not user administration: refused, and the denial names both people.
        using (var refused = await SendAsync(HttpMethod.Post, "/api/v1/roles", graceCookie, new { code = "acting-test", name = "Acting test", description = "", principal_type = "user", permissions = Array.Empty<string>() }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var events = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.ActorId == grace).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(administrator, Assert.Single(events, e => e.EventType == IdentityAuditEvents.ActingStarted).OnBehalfOfId);
            Assert.Equal(administrator, Assert.Single(events, e => e.EventType == AuthorizationAuditEvents.PermissionDenied && e.Target == "POST /api/v1/roles").OnBehalfOfId);
        }

        // The audit list shows both, by name.
        using (var audit = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/audit-events?filter=on_behalf_of_id:eq:{administrator}", administratorCookie)))
        {
            var row = audit.RootElement.GetProperty("data").EnumerateArray().First(e => e.GetProperty("event_type").GetString() == IdentityAuditEvents.ActingStarted);
            Assert.Equal(("grace", "admin"), (row.GetProperty("actor_name").GetString(), row.GetProperty("on_behalf_of_name").GetString()));
        }

        // Stopping: her own rights again.
        using (var stopped = await JsonAsync(await SendAsync(HttpMethod.Delete, "/api/v1/me/acting-for", graceCookie)))
        {
            Assert.Equal(JsonValueKind.Null, stopped.RootElement.GetProperty("acting_for").ValueKind);
        }

        using var again = await SendAsync(HttpMethod.Get, "/api/v1/users", graceCookie);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
    }

    [Fact]
    [Proves("ADR-0037")]
    public async Task WhenTheDelegationEndsTheNextRequestActsAsThePersonAndSaysSo()
    {
        var (administrator, administratorCookie) = await PersonAsync("admin", SystemRoles.TenantAdministrator);
        var (grace, graceCookie) = await PersonAsync("grace", null);
        using (var created = await SendAsync(HttpMethod.Post, "/api/v1/delegations", administratorCookie, new
        {
            delegate_id = grace,
            duties = new[] { "identity.user_administration" },
            starts_at = _time.GetUtcNow().AddMinutes(-1),
            ends_at = _time.GetUtcNow().AddMinutes(20),
            reason = "A short cover.",
        }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using (var acting = await SendAsync(HttpMethod.Post, "/api/v1/me/acting-for", graceCookie, new { principal_id = administrator }))
        {
            Assert.Equal(HttpStatusCode.OK, acting.StatusCode);
        }

        _time.Advance(TimeSpan.FromMinutes(21));
        using (var users = await SendAsync(HttpMethod.Get, "/api/v1/users", graceCookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
            Assert.Equal(administrator.ToString("D"), Assert.Single(users.Headers.GetValues(SessionMiddleware.ActingEndedHeader)));
        }

        using (var me = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me", graceCookie)))
        {
            Assert.Equal(JsonValueKind.Null, me.RootElement.GetProperty("acting_for").ValueKind);
            Assert.Empty(me.RootElement.GetProperty("delegations").GetProperty("received").EnumerateArray());
        }

        // Nothing to act with any more: refused, with its code.
        using var refused = await SendAsync(HttpMethod.Post, "/api/v1/me/acting-for", graceCookie, new { principal_id = administrator });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(IdentityErrors.ActingNoDelegation, await ErrorCodeAsync(refused));

        await using var scope = _app!.Services.CreateAsyncScope();
        var ended = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().SingleAsync(e => e.ActorId == grace && e.EventType == IdentityAuditEvents.ActingEnded, TestContext.Current.CancellationToken);
        Assert.Equal(administrator, ended.OnBehalfOfId);
    }

    [Fact]
    [Proves("ADR-0037")]
    public async Task TheDirectoryAnswersADisplayNameAndAHomeUnitNameAPageAtATimeForTwoCharactersOrMore()
    {
        var tag = $"dir{Ids.New():N}"[..12];
        var (_, cookie) = await PersonAsync($"{tag}a", role: null);
        await PersonAsync($"{tag}b", role: null);
        await PersonAsync($"{tag}c", role: null);

        using var tooShort = await SendAsync(HttpMethod.Get, "/api/v1/directory?search=d", cookie);
        using var first = await SendAsync(HttpMethod.Get, $"/api/v1/directory?search={tag}&page_size=2", cookie);
        using var second = await SendAsync(HttpMethod.Get, $"/api/v1/directory?search={tag}&page_size=2&page_number=2", cookie);
        using var asked = await SendAsync(HttpMethod.Get, $"/api/v1/directory?search={tag}&fields=login_id", cookie);

        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(IdentityErrors.DirectorySearchTooShort, JsonDocument.Parse(await tooShort.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("error_code").GetString());
        var page = JsonDocument.Parse(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal(3, page.GetProperty("meta").GetProperty("total_count").GetInt32());
        var rows = page.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal([$"{tag}a", $"{tag}b"], rows.Select(row => row.GetProperty("display_name").GetString()));

        // A name, a home unit and the id a delegation names the person by: no login, email, status or type.
        Assert.All(rows, row => Assert.Equal(["id", "display_name", "home_unit"], row.EnumerateObject().Select(property => property.Name)));
        var rest = JsonDocument.Parse(await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("data");
        Assert.Equal([$"{tag}c"], rest.EnumerateArray().Select(row => row.GetProperty("display_name").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, asked.StatusCode);
    }

    [Fact]
    [Proves("ADR-0037")]
    public async Task ATokenNeverActsForAnyone()
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        var token = new SignedInPrincipal(Ids.New(), "client", "A client", Data.PrincipalType.ServiceAccount, Ids.New(), _time.GetUtcNow().AddHours(1), _time.GetUtcNow().AddHours(1), AuthenticationKind.Token);

        var refused = await scope.ServiceProvider.GetRequiredService<SessionService>().ActForAsync(token, Ids.New(), TestContext.Current.CancellationToken);

        Assert.Equal(IdentityErrors.ActingToken, refused.Error.Code);
        Assert.Null(new RequestPrincipalProbe(token).ActingForId);
    }

    private async Task<(Guid Id, string Cookie)> PersonAsync(string name, string? role)
    {
        var loginId = $"{name}-{Ids.New():N}@acme.test";
        Guid id;
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, loginId, Password, name);
            if (role is not null)
            {
                Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(user.Id, role, TestContext.Current.CancellationToken)).IsSuccess);
            }

            id = user.Id;
        }

        using var login = await _client.PostAsJsonAsync("/auth/login", new { login_id = loginId, password = Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (id, Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0]);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string cookie, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("Cookie", cookie);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
            return JsonDocument.Parse(text);
        }
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return problem.RootElement.GetProperty("error_code").GetString();
    }

    /// <summary>The request principal of a request made with a token: it never acts for anyone.</summary>
    private sealed class RequestPrincipalProbe(SignedInPrincipal signedIn)
    {
        public Guid? ActingForId
        {
            get
            {
                var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
                RequestPrincipal.Set(context, signedIn with { ActingForId = Ids.New() });
                return new RequestPrincipal(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = context }).ActingForId;
            }
        }
    }
}
