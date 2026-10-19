using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Telemetry;
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
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new AuthorizationModule(), new IdentityModule()]);

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
    public async Task WithoutACookieProtectedEndpointsAnswerNotAuthenticated()
    {
        using var me = await Get("/api/v1/me", cookie: null);

        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(PermissionErrors.NotAuthenticated, await ErrorCode(me));
    }

    [Fact]
    public async Task AnInvitationIsAcceptedOverHttpAndTheAccountCanLogIn()
    {
        string token;
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            token = await IdentityFixture.ReinviteAdministratorAsync(scope.ServiceProvider);
        }

        using var tooShort = await _client.PostAsJsonAsync($"/auth/invitations/{token}/accept", new { password = "short" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(IdentityErrors.PasswordTooShort, await ErrorCode(tooShort));

        using var accepted = await _client.PostAsJsonAsync($"/auth/invitations/{token}/accept", new { password = Password, display_name = "Acme Admin" }, TestContext.Current.CancellationToken);
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

        using var unknown = await _client.PostAsJsonAsync($"/auth/invitations/{SecureTokens.New()}/accept", new { password = Password }, TestContext.Current.CancellationToken);
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

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return problem.RootElement.GetProperty("error_code").GetString();
    }
}
