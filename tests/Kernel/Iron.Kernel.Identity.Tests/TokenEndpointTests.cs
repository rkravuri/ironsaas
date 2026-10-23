using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.OpenApi;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization;
using Iron.Kernel.Compliance;
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

/// <summary>The done-when of T11 over HTTP: the smoke client gets a token and lists users; wrong tenant, expiry and bad secrets are refused; OpenAPI names the permission.</summary>
[Collection(IdentityCollection.Name)]
public class TokenEndpointTests(IdentityFixture fixture) : IAsyncLifetime
{
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
        builder.Services.AddIronOpenApi("Test API");
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new AuthorizationModule(), new IdentityModule(), new SettingsModule(), new ComplianceModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.UseIronIdentity();
        _app.MapOpenApi().AllowAnonymous();
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
    [Proves("TF-020 AC2", "TF-023 AC1", "ADR-0015")]
    public async Task TheSmokeClientGetsATokenAndListsUsersUntilTheTokenExpires()
    {
        using var response = await RequestToken(IdentityFixture.SmokeClientId, IdentityFixture.SmokeClientSecret);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var token = body.RootElement.GetProperty("access_token").GetString()!;
        Assert.Equal("Bearer", body.RootElement.GetProperty("token_type").GetString());
        Assert.Equal(900, body.RootElement.GetProperty("expires_in").GetInt32());

        using var users = await Get("/api/v1/users?sort=login_id&fields=login_id,type,status", token);
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        using var list = JsonDocument.Parse(await users.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var rows = list.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.Contains(rows, r => r.GetProperty("login_id").GetString() == IdentityFixture.SmokeClientId && r.GetProperty("type").GetString() == "service_account");
        Assert.Contains(rows, r => r.GetProperty("login_id").GetString() == IdentityFixture.AdministratorLoginId);
        Assert.All(rows, r => Assert.False(r.TryGetProperty("password_hash", out _)));

        using var me = await Get("/api/v1/me", token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // service-default holds every read permission, so the audit trail is readable too.
        using var auditEvents = await Get("/api/v1/audit-events", token);
        Assert.Equal(HttpStatusCode.OK, auditEvents.StatusCode);

        _time.Advance(TimeSpan.FromMinutes(16));
        using var expired = await Get("/api/v1/users", token);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(PermissionErrors.TokenExpired, await ErrorCode(expired));
        Assert.Contains("invalid_token", expired.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0015")]
    public async Task AWrongSecretOrGrantIsRefusedInTheOAuthShapeAndAudited()
    {
        using var wrongSecret = await RequestToken(IdentityFixture.SmokeClientId, "not the secret");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        using var error = JsonDocument.Parse(await wrongSecret.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("invalid_client", error.RootElement.GetProperty("error").GetString());
        Assert.Equal(32, error.RootElement.GetProperty("correlation_id").GetString()?.Length);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "password", ["client_id"] = IdentityFixture.SmokeClientId, ["client_secret"] = IdentityFixture.SmokeClientSecret });
        using var badGrant = await _client.PostAsync(new Uri("/connect/token", UriKind.Relative), content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, badGrant.StatusCode);
        using var grantError = JsonDocument.Parse(await badGrant.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("unsupported_grant_type", grantError.RootElement.GetProperty("error").GetString());

        await using var scope = _app!.Services.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.True(await audit.Events.AsNoTracking().AnyAsync(e => e.EventType == IdentityAuditEvents.TokenRefused && e.Target == IdentityFixture.SmokeClientId && e.Reason == "wrong_secret", TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("TF-003 AC1", "ADR-0015")]
    public async Task ATokenForAnotherTenantIsRefused()
    {
        string token;
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var client = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"stranger-{Ids.New():N}", "a secret of some length");
            var bravo = new TokenService(
                scope.ServiceProvider.GetRequiredService<SigningKeys>(),
                scope.ServiceProvider.GetRequiredService<TokenOptions>(),
                new TenantContext(TenantId.Parse("bravo"), ["bravo.localhost"]),
                _time);
            token = bravo.Issue(client, null).AccessToken;
        }

        using var response = await Get("/api/v1/users", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(PermissionErrors.TokenInvalid, await ErrorCode(response));
    }

    [Fact]
    [Proves("ADR-0038")]
    public async Task ATokenWorksForNoUnit()
    {
        using var response = await RequestToken(IdentityFixture.SmokeClientId, IdentityFixture.SmokeClientSecret);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var token = body.RootElement.GetProperty("access_token").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/me/working-unit", UriKind.Relative)) { Content = new StringContent("{\"all_units\":true}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var refused = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        using var me = await Get("/api/v1/me", token);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(IdentityErrors.WorkingUnitToken, await ErrorCode(refused));
        using var described = JsonDocument.Parse(await me.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, described.RootElement.GetProperty("working_unit").ValueKind);
        Assert.Empty(described.RootElement.GetProperty("working_units").GetProperty("units").EnumerateArray());
    }

    [Fact]
    [Proves("ADR-0015")]
    public async Task TheKeysAreOnTheJwksEndpointAndOpenApiNamesThePermission()
    {
        using var jwks = await _client.GetAsync(new Uri("/.well-known/jwks.json", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, jwks.StatusCode);
        using var keys = JsonDocument.Parse(await jwks.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var key = Assert.Single(keys.RootElement.GetProperty("keys").EnumerateArray());
        Assert.Equal("EC", key.GetProperty("kty").GetString());
        Assert.Equal("ES256", key.GetProperty("alg").GetString());
        Assert.False(key.TryGetProperty("d", out _));

        using var openApi = await _client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        using var document = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/v1/users").GetProperty("get");
        Assert.Contains($"Requires permission `{IdentityPermissions.UsersRead.Name}`", operation.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> RequestToken(string clientId, string secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/connect/token", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "read" });
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> Get(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return problem.RootElement.GetProperty("error_code").GetString();
    }
}
