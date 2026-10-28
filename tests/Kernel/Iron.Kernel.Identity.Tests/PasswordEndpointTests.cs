using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Compliance;
using Iron.Kernel.Data;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Settings;
using Iron.Kernel.Settings.Services;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Identity.Tests;

/// <summary>The password and life-cycle endpoints over HTTP (S3 T2, ADR-0046): who may do what, and what each answers.</summary>
[Collection(IdentityCollection.Name)]
public class PasswordEndpointTests(IdentityFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a brand new passphrase";

    private readonly FakeTimeProvider _time = new();
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync() => (_app, _client) = await StartAsync(fixture.ConnectionString, _time);

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    [Proves("ADR-0046")]
    public async Task ThePolicyIsReadWithoutSigningInSoTheFormsCanSayItBeforeAnyoneTypes()
    {
        using var answer = await _client.GetAsync(new Uri("/auth/password-policy", UriKind.Relative), TestContext.Current.CancellationToken);
        using var body = await JsonAsync(answer);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var policy = body.RootElement;
        Assert.Equal((12, 1024, true, 10, 0, 14), (policy.GetProperty("minimum_length").GetInt32(), policy.GetProperty("maximum_length").GetInt32(), policy.GetProperty("common_check").GetBoolean(), policy.GetProperty("history_depth").GetInt32(), policy.GetProperty("maximum_age_days").GetInt32(), policy.GetProperty("warning_days").GetInt32()));
    }

    [Fact]
    [Proves("TF-012 AC2", "ADR-0031", "ADR-0046")]
    public async Task AnAdministratorOfThePersonsUnitUnlocksTheirLoginAndAReaderCannot()
    {
        var (unit, other) = await UnitsAsync();
        var person = await UserAsync(unit);
        var administrator = await UserAsync(other);
        var reader = await UserAsync(other);
        await GrantAsync(administrator.Id, [IdentityPermissions.UsersRead.Name, IdentityPermissions.UsersManage.Name], RoleScope.Unit(unit));
        await GrantAsync(reader.Id, [IdentityPermissions.UsersRead.Name], RoleScope.Unit(unit));
        string? lastAnswer = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var failed = await _client.PostAsJsonAsync("/auth/login", new { login_id = person.LoginId, password = attempt == 5 ? Password : "not the password" }, TestContext.Current.CancellationToken);
            lastAnswer = await ProblemAsync(failed, HttpStatusCode.Unauthorized);
        }

        var asAdministrator = await LoginAsync(administrator.LoginId);
        var asReader = await LoginAsync(reader.LoginId);
        using var lockedRecord = await SendAsync(HttpMethod.Get, $"/api/v1/users/{person.Id}", asAdministrator);
        using var lockedBody = await JsonAsync(lockedRecord);
        using var byReader = await SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/unlock", asReader);
        using var byAdministrator = await SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/unlock", asAdministrator);
        using var unlockedBody = await JsonAsync(byAdministrator);
        using var twice = await SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/unlock", asAdministrator);
        using var signedIn = await _client.PostAsJsonAsync("/auth/login", new { login_id = person.LoginId, password = Password }, TestContext.Current.CancellationToken);

        Assert.Equal(IdentityErrors.LoginLocked, lastAnswer); // the sixth, with the right password
        Assert.Equal(JsonValueKind.String, lockedBody.RootElement.GetProperty("locked_until").ValueKind);
        Assert.Equal(PermissionErrors.PermissionDenied, await ProblemAsync(byReader, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.OK, byAdministrator.StatusCode);
        Assert.Equal(JsonValueKind.Null, unlockedBody.RootElement.GetProperty("locked_until").ValueKind);
        Assert.Equal(IdentityErrors.UserStatusConflict, await ProblemAsync(twice, HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    [Fact]
    [Proves("TF-017 AC1", "TF-017 AC2", "ADR-0046")]
    public async Task ARecoveryLinkFromAnAdministratorIsShownOnceAndWorksOnceWithoutSigningIn()
    {
        var (unit, _) = await UnitsAsync();
        var person = await UserAsync(unit);
        var administrator = await UserAsync(unit);
        await GrantAsync(administrator.Id, [IdentityPermissions.UsersRead.Name, IdentityPermissions.UsersManage.Name], RoleScope.Unit(unit));
        var asAdministrator = await LoginAsync(administrator.LoginId);

        using var issued = await SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/recovery-link", asAdministrator);
        using var link = await JsonAsync(issued);
        var path = link.RootElement.GetProperty("recovery_link").GetString()!;
        var code = path[(path.IndexOf('#', StringComparison.Ordinal) + 1)..];
        using var common = await _client.PostAsJsonAsync("/auth/recovery", new { token = code, password = IdentityFixture.CommonPassword() }, TestContext.Current.CancellationToken);
        using var set = await _client.PostAsJsonAsync("/auth/recovery", new { token = code, password = NewPassword }, TestContext.Current.CancellationToken);
        using var again = await _client.PostAsJsonAsync("/auth/recovery", new { token = code, password = "another new passphrase" }, TestContext.Current.CancellationToken);
        using var signedIn = await _client.PostAsJsonAsync("/auth/login", new { login_id = person.LoginId, password = NewPassword }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        Assert.StartsWith("/recovery#", path, StringComparison.Ordinal); // the code in the fragment, which no server receives (ADR-0046)
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(15), link.RootElement.GetProperty("expires_at").GetDateTimeOffset());
        Assert.Equal(IdentityErrors.PasswordCommon, await ProblemAsync(common, HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(IdentityErrors.RecoveryInvalid, await ProblemAsync(again, HttpStatusCode.NotFound));
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    [Fact]
    [Proves("ADR-0031", "ADR-0046")]
    public async Task AnAdministratorCannotIssueALinkForSomeoneWhosePermissionsTheyDoNotHold()
    {
        var (unit, _) = await UnitsAsync();
        var tenantAdministrator = await UserAsync(unit);
        var unitAdministrator = await UserAsync(unit);
        await GrantAsync(tenantAdministrator.Id, [AuthorizationPermissions.AssignmentsManage.Name], RoleScope.Tenant);
        await GrantAsync(unitAdministrator.Id, [IdentityPermissions.UsersRead.Name, IdentityPermissions.UsersManage.Name], RoleScope.Unit(unit));
        var asUnitAdministrator = await LoginAsync(unitAdministrator.LoginId);

        using var refused = await SendAsync(HttpMethod.Post, $"/api/v1/users/{tenantAdministrator.Id}/recovery-link", asUnitAdministrator);
        using var problem = await JsonAsync(refused);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(IdentityErrors.RecoveryEscalation, problem.RootElement.GetProperty("error_code").GetString());
        Assert.Contains(AuthorizationPermissions.AssignmentsManage.Name, problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Proves("TF-017 AC3", "TF-017 AC4", "ADR-0046")]
    public async Task ChangingThePasswordEndsTheOtherSessionsAndTheChangeIsInTheSecurityActivity()
    {
        var (unit, _) = await UnitsAsync();
        var person = await UserAsync(unit);
        var here = await LoginAsync(person.LoginId);
        var elsewhere = await LoginAsync(person.LoginId);

        using var wrong = await SendAsync(HttpMethod.Post, "/api/v1/me/password", here, new { current_password = "not the password", new_password = NewPassword });
        using var changed = await SendAsync(HttpMethod.Post, "/api/v1/me/password", here, new { current_password = Password, new_password = NewPassword });
        using var hereAfter = await SendAsync(HttpMethod.Get, "/api/v1/me", here);
        using var elsewhereAfter = await SendAsync(HttpMethod.Get, "/api/v1/me", elsewhere);
        using var activity = await SendAsync(HttpMethod.Get, "/api/v1/me/security-events", here);
        using var events = await JsonAsync(activity);

        Assert.Equal(IdentityErrors.PasswordCurrentWrong, await ProblemAsync(wrong, HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, hereAfter.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, elsewhereAfter.StatusCode);
        var first = events.RootElement.GetProperty("data")[0];
        Assert.Equal((SecurityEventKinds.PasswordChanged, SecurityEventActors.Self), (first.GetProperty("kind").GetString(), first.GetProperty("actor").GetString()));
    }

    [Fact]
    [Proves("ADR-0046")]
    public async Task SecurityActivityListsOnlyTheirOwnEventsNewestFirst()
    {
        var (unit, _) = await UnitsAsync();
        var person = await UserAsync(unit);
        var someoneElse = await UserAsync(unit);
        var administrator = await UserAsync(unit);
        await GrantAsync(administrator.Id, [IdentityPermissions.UsersRead.Name, IdentityPermissions.UsersManage.Name], RoleScope.Unit(unit));
        var asAdministrator = await LoginAsync(administrator.LoginId);
        using (await SendAsync(HttpMethod.Post, $"/api/v1/users/{someoneElse.Id}/recovery-link", asAdministrator))
        {
        }

        using (await SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/recovery-link", asAdministrator))
        {
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        var asPerson = await LoginAsync(person.LoginId);
        using (await SendAsync(HttpMethod.Post, "/api/v1/me/password", asPerson, new { current_password = Password, new_password = NewPassword, end_other_sessions = false }))
        {
        }

        using var activity = await SendAsync(HttpMethod.Get, "/api/v1/me/security-events", asPerson);
        using var events = await JsonAsync(activity);
        var rows = events.RootElement.GetProperty("data").EnumerateArray().ToList();

        Assert.Equal([SecurityEventKinds.PasswordChanged, SecurityEventKinds.RecoveryLinkIssued], rows.Select(r => r.GetProperty("kind").GetString()));
        Assert.Equal((SecurityEventActors.Administrator, administrator.DisplayName), (rows[1].GetProperty("actor").GetString(), rows[1].GetProperty("actor_name").GetString()));
    }

    [Fact]
    [Proves("TF-010 AC3", "ADR-0046")]
    public async Task DeactivationEndsAPersonsSessionsAndRefusesAServiceAccountsTokenAndReactivationRestores()
    {
        var (unit, _) = await UnitsAsync();
        var person = await UserAsync(unit);
        var administrator = await UserAsync(unit);
        await GrantAsync(administrator.Id, [IdentityPermissions.UsersRead.Name, IdentityPermissions.UsersManage.Name], RoleScope.Unit(unit));
        var (client, secret) = await ServiceAccountAsync(unit);
        var token = await TokenAsync(client.LoginId, secret);
        var asPerson = await LoginAsync(person.LoginId);
        var asAdministrator = await LoginAsync(administrator.LoginId);

        using var tokenBefore = await SendAsync(HttpMethod.Get, "/api/v1/me", bearer: token);
        using var deactivatedPerson = await SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/deactivate", asAdministrator);
        using var deactivatedClient = await SendAsync(HttpMethod.Post, $"/api/v1/users/{client.Id}/deactivate", asAdministrator);
        using var personAfter = await SendAsync(HttpMethod.Get, "/api/v1/me", asPerson);
        using var tokenAfter = await SendAsync(HttpMethod.Get, "/api/v1/me", bearer: token);
        using var signIn = await _client.PostAsJsonAsync("/auth/login", new { login_id = person.LoginId, password = Password }, TestContext.Current.CancellationToken);
        using var reactivated = await SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/reactivate", asAdministrator);
        using var reactivatedBody = await JsonAsync(reactivated);
        using var signInAgain = await _client.PostAsJsonAsync("/auth/login", new { login_id = person.LoginId, password = Password }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, tokenBefore.StatusCode);
        Assert.Equal(HttpStatusCode.OK, deactivatedPerson.StatusCode);
        Assert.Equal(HttpStatusCode.OK, deactivatedClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, personAfter.StatusCode);
        Assert.Equal(PermissionErrors.TokenInvalid, await ProblemAsync(tokenAfter, HttpStatusCode.Unauthorized));
        Assert.Equal(IdentityErrors.LoginInvalid, await ProblemAsync(signIn, HttpStatusCode.Unauthorized));
        Assert.Equal("active", reactivatedBody.RootElement.GetProperty("user").GetProperty("status").GetString(), ignoreCase: true);
        Assert.Equal(HttpStatusCode.OK, signInAgain.StatusCode);
    }

    [Fact]
    [Proves("TF-012 AC5", "ADR-0046")]
    public async Task ASignInWithAnExpiredPasswordAnswersARecoveryCodeAndNoSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var tenant = await fixture.CreateTenantDatabaseAsync($"expiry_{Ids.New():N}");
        var (app, client) = await StartAsync(tenant, time);
        await using var _ = app;
        using var __ = client;
        string loginId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<SettingsWriter>().SetAsync(IdentitySettings.PasswordMaximumAgeDays.Key, SettingPlace.Tenant, JsonSerializer.SerializeToElement(30), ct)).IsSuccess);
            var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"expiry-{Ids.New():N}@acme.test", Password);
            Assert.True((await scope.ServiceProvider.GetRequiredService<PasswordService>().ChangeAsync(user.Id, null, Password, NewPassword, false, ct)).IsSuccess);
            loginId = user.LoginId;
        }

        using var young = await client.PostAsJsonAsync("/auth/login", new { login_id = loginId, password = NewPassword }, ct);
        var cookie = Assert.Single(young.Headers.GetValues("Set-Cookie")).Split(';')[0];
        using var meYoung = await SendWithAsync(client, HttpMethod.Get, "/api/v1/me", cookie);
        using var meBody = await JsonAsync(meYoung);
        time.Advance(TimeSpan.FromDays(30));
        using var expired = await client.PostAsJsonAsync("/auth/login", new { login_id = loginId, password = NewPassword }, ct);
        using var problem = await JsonAsync(expired);
        var code = problem.RootElement.GetProperty("recovery_token").GetString();
        using var renewed = await client.PostAsJsonAsync("/auth/recovery", new { token = code, password = "a renewed passphrase" }, ct);

        Assert.Equal(time.GetUtcNow(), meBody.RootElement.GetProperty("password_expires_at").GetDateTimeOffset());
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(IdentityErrors.PasswordExpired, problem.RootElement.GetProperty("error_code").GetString());
        Assert.False(expired.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
    }

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(string connectionString, FakeTimeProvider time)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(IdentityFixture.Configuration);
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton(IdentityFixture.Tenant);
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(connectionString);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new AuthorizationModule(), new IdentityModule(), new SettingsModule(), new ComplianceModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

        var app = builder.Build();
        app.UseIronTelemetry();
        app.UseIronApi();
        app.UseIronIdentity();
        app.MapModules();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, app.GetTestClient());
    }

    private async Task<(Guid Unit, Guid Other)> UnitsAsync()
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = (await units.RootAsync(TestContext.Current.CancellationToken))!.Id;
        var unit = await units.CreateAsync(root, "Passwords", $"p-{Ids.New():N}", KernelOrganisationKinds.Unit, TestContext.Current.CancellationToken);
        var other = await units.CreateAsync(root, "Elsewhere", $"e-{Ids.New():N}", KernelOrganisationKinds.Unit, TestContext.Current.CancellationToken);
        return (unit.Value.Id, other.Value.Id);
    }

    private async Task<Principal> UserAsync(Guid homeUnit)
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = new Principal($"pw-{Ids.New():N}@acme.test", PrincipalType.User, $"Person {Ids.New():N}"[..15], null);
        user.AcceptInvitation(scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), null);
        user.SetHomeUnit(homeUnit);
        context.Principals.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    private async Task<(Principal Client, string Secret)> ServiceAccountAsync(Guid homeUnit)
    {
        const string Secret = "a service account secret of some length";
        await using var scope = _app!.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var client = new Principal($"client-{Ids.New():N}", PrincipalType.ServiceAccount, "Leaving client", null);
        client.AcceptInvitation(scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Secret), null);
        client.SetHomeUnit(homeUnit);
        context.Principals.Add(client);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (client, Secret);
    }

    private async Task<string> TokenAsync(string clientId, string secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/connect/token", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
        using var answer = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        using var body = await JsonAsync(answer);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return body.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task GrantAsync(Guid principal, IReadOnlyList<string> permissions, RoleScope scope)
    {
        await using var services = _app!.Services.CreateAsyncScope();
        var context = services.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var role = new Role($"r-{Ids.New():N}", "Password test role", string.Empty, isSystem: false, PrincipalKind.User, permissions);
        context.Roles.Add(role);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var result = await services.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, role.Code, scope, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }

    private async Task<string> LoginAsync(string loginId)
    {
        using var login = await _client.PostAsJsonAsync("/auth/login", new { login_id = loginId, password = Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? cookie = null, object? body = null, string? bearer = null) =>
        SendWithAsync(_client, method, path, cookie, body, bearer);

    private static async Task<HttpResponseMessage> SendWithAsync(HttpClient client, HttpMethod method, string path, string? cookie = null, object? body = null, string? bearer = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static async Task<string?> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = await JsonAsync(response);
        return problem.RootElement.GetProperty("error_code").GetString();
    }
}
