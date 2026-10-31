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
using Iron.Kernel.Compliance;
using Iron.Kernel.Data;
using Iron.Kernel.Events;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Settings;
using Iron.Kernel.Telemetry;
using Iron.Kernel.Telemetry.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Identity.Tests;

/// <summary>The done-when of T10 over HTTP: login, the cookie, me, a protected endpoint through the cookie, expiry, logout.</summary>
[Collection(IdentityCollection.Name)]
public class AuthEndpointTests(IdentityFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string CookieName = "iron.session";

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
        builder.Services.AddIronHealth();
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new IdentityModule(), new SettingsModule(), new ComplianceModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.UseIronIdentity();
        _app.MapModules();
        _app.MapIronHealth();
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
    [Proves("TF-011 AC1", "TF-021 AC1", "TF-029 AC3", "ADR-0014", "ADR-0033")]
    public async Task TheFullSessionLifeOverHttp()
    {
        var loginId = $"http-{Ids.New():N}@acme.test";
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, loginId, Password);
            Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(user.Id, SystemRoles.ReadOnly, TestContext.Current.CancellationToken)).IsSuccess);
        }

        // Wrong password: the fixed message, no cookie.
        using var wrong = await _client.PostAsJsonAsync("/auth/login", new { login_id = loginId, password = "wrong" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(IdentityErrors.LoginInvalid, await ErrorCode(wrong));
        Assert.False(wrong.Headers.Contains("Set-Cookie"));

        // Right password: the session and the cookie.
        using var login = await _client.PostAsJsonAsync("/auth/login", new { login_id = loginId, password = Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(CookieName + "=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        var cookie = setCookie.Split(';')[0];
        using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(loginId, loginBody.RootElement.GetProperty("principal").GetProperty("login_id").GetString());
        Assert.Equal("active", loginBody.RootElement.GetProperty("principal").GetProperty("status").GetString());

        // Me: who, permissions and the expiry the shell warns about.
        using var me = await Get("/api/v1/me", cookie);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var meBody = JsonDocument.Parse(await me.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(loginId, meBody.RootElement.GetProperty("login_id").GetString());
        Assert.Contains(AuthorizationPermissions.RolesRead.Name, meBody.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(60), meBody.RootElement.GetProperty("session").GetProperty("idle_expires_at").GetDateTimeOffset());
        // My profile shows when the session started (D4): the login's moment, on the fake clock.
        Assert.Equal(_time.GetUtcNow(), meBody.RootElement.GetProperty("session").GetProperty("started_at").GetDateTimeOffset());

        // The cookie principal passes the permission filter of another module.
        using var roles = await Get("/api/v1/roles", cookie);
        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);

        // Idle for 61 minutes: the session-expired problem, and the cookie is cleared.
        _time.Advance(TimeSpan.FromMinutes(61));
        using var expired = await Get("/api/v1/me", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(PermissionErrors.SessionExpired, await ErrorCode(expired));
        Assert.Contains(expired.Headers.GetValues("Set-Cookie"), c => c.StartsWith(CookieName + "=", StringComparison.Ordinal) && c.Contains("expires=", StringComparison.OrdinalIgnoreCase));

        // Log in again and log out: the session ends server-side and the old cookie serves nothing (TF-029 AC3).
        using var second = await _client.PostAsJsonAsync("/auth/login", new { login_id = loginId, password = Password }, TestContext.Current.CancellationToken);
        var secondCookie = Assert.Single(second.Headers.GetValues("Set-Cookie")).Split(';')[0];
        using var logout = await Post("/auth/logout", secondCookie);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var afterLogout = await Get("/api/v1/me", secondCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
        Assert.Equal(PermissionErrors.SessionExpired, await ErrorCode(afterLogout));
    }

    [Fact]
    [Proves("TF-031 AC3", "ADR-0014")]
    public async Task TheAnonymousOperationsWorkWithoutATokenAndShowNothingOfTheTenant()
    {
        var loginId = $"anonymous-{Ids.New():N}@acme.test";
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, loginId, Password, "Anonymous Probe");
        }

        var ct = TestContext.Current.CancellationToken;
        using var wrongPassword = await _client.PostAsJsonAsync("/auth/login", new { login_id = loginId, password = "wrong" }, ct);
        using var unknownLogin = await _client.PostAsJsonAsync("/auth/login", new { login_id = $"nobody-{Ids.New():N}@example.org", password = "wrong" }, ct);
        using var invitation = await _client.PostAsJsonAsync("/auth/invitations/accept", new { token = Ids.New().ToString("N"), password = Password, display_name = "Somebody" }, ct);
        using var token = await _client.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = "nobody", ["client_secret"] = "nothing" }), ct);
        using var keys = await Get("/.well-known/jwks.json", cookie: null);
        using var live = await Get("/health/live", cookie: null);
        using var ready = await Get("/health/ready", cookie: null);

        // Each works without a token: it answers as it is meant to, never "not authenticated".
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(IdentityErrors.LoginInvalid, await ErrorCode(wrongPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, unknownLogin.StatusCode);
        Assert.Equal(IdentityErrors.LoginInvalid, await ErrorCode(unknownLogin));
        Assert.True(invitation.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Gone, $"invitation: {invitation.StatusCode}");
        Assert.Equal(HttpStatusCode.Unauthorized, token.StatusCode);
        Assert.Equal(HttpStatusCode.OK, keys.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        // Nothing tenant-specific: not the tenant, its hostname, a person's login or name, or the database. An unknown
        // login and a wrong password read the same, so the answer does not say whether an account exists.
        var database = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString).Database!;
        string[] secrets = ["acme", IdentityFixture.Tenant.PrimaryHostname, loginId, "Anonymous Probe", IdentityFixture.AdministratorDisplayName, database, "Host="];
        foreach (var answer in new[] { wrongPassword, unknownLogin, invitation, token, keys, live, ready })
        {
            var body = await answer.Content.ReadAsStringAsync(ct);
            foreach (var secret in secrets)
            {
                Assert.False(body.Contains(secret, StringComparison.OrdinalIgnoreCase), $"{answer.RequestMessage?.RequestUri} answered with '{secret}': {body}");
            }
        }

        Assert.Equal(await DetailOf(wrongPassword), await DetailOf(unknownLogin));
    }

    [Fact]
    [Proves("ADR-0014")]
    public async Task WithoutACookieProtectedEndpointsAnswerNotAuthenticated()
    {
        using var me = await Get("/api/v1/me", cookie: null);

        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(PermissionErrors.NotAuthenticated, await ErrorCode(me));
    }

    [Fact]
    [Proves("TF-018 AC1", "ADR-0014")]
    public async Task AnInvitationIsAcceptedOverHttpAndTheAccountCanLogIn()
    {
        string token;
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            token = await IdentityFixture.ReinviteAdministratorAsync(scope.ServiceProvider);
        }

        using var tooShort = await _client.PostAsJsonAsync("/auth/invitations/accept", new { token, password = "short" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(IdentityErrors.PasswordTooShort, await ErrorCode(tooShort));

        using var accepted = await _client.PostAsJsonAsync("/auth/invitations/accept", new { token, password = Password, display_name = "Acme Admin" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var login = await _client.PostAsJsonAsync("/auth/login", new { login_id = IdentityFixture.AdministratorLoginId, password = Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
        using var me = await Get("/api/v1/me", cookie);
        using var meBody = JsonDocument.Parse(await me.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var permissions = meBody.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains(IdentityPermissions.UsersManage.Name, permissions);
        Assert.Contains(AuthorizationPermissions.RolesManage.Name, permissions);
        var homeUnit = meBody.RootElement.GetProperty("home_unit");
        Assert.Equal(IdentityFixture.Tenant.Id.Value, homeUnit.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.String, homeUnit.GetProperty("name").ValueKind);

        using var unknown = await _client.PostAsJsonAsync("/auth/invitations/accept", new { token = SecureTokens.New(), password = Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private async Task<HttpResponseMessage> Get(string path, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> Post(string path, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add("Cookie", cookie);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string?> DetailOf(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return problem.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return problem.RootElement.GetProperty("error_code").GetString();
    }
}
