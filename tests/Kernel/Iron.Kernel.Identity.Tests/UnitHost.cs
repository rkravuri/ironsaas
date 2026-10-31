using System.Net;
using System.Net.Http.Headers;
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
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Compliance;
using Iron.Kernel.Data;
using Iron.Kernel.Events;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Security;
using Iron.Kernel.Settings;
using Iron.Kernel.Settings.Services;
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
/// One in-memory unit over HTTP on a tenant database of its own, with its own clock, the security headers, and the steps
/// the tests of sessions, revocation, impersonation, emergency accounts and terms repeat (S3 T5). A second host on the
/// same database is another API process of the same unit: it shares the rows, not the caches.
/// </summary>
public sealed class UnitHost : IAsyncDisposable
{
    public const string Password = "correct horse battery staple";
    public const string SessionCookie = "iron.session";
    public const string SignInCookie = "iron.sign_in";

    private readonly WebApplication _app;
    private readonly HttpClient _client;

    private UnitHost(WebApplication app, FakeTimeProvider time, string connectionString)
    {
        _app = app;
        _client = app.GetTestClient();
        Time = time;
        ConnectionString = connectionString;
    }

    public FakeTimeProvider Time { get; }

    public string ConnectionString { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>
    /// A unit on a new tenant database (or <paramref name="connectionString"/>'s, for a second process of the same unit),
    /// with <paramref name="configuration"/> over the fixture's and <paramref name="services"/> replacing what a test fakes.
    /// </summary>
    public static async Task<UnitHost> StartAsync(IdentityFixture fixture, string? connectionString = null, IReadOnlyDictionary<string, string?>? configuration = null, Action<IServiceCollection>? services = null, FakeTimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        connectionString ??= await fixture.CreateTenantDatabaseAsync($"unit_{Ids.New():N}");
        time ??= new FakeTimeProvider(DateTimeOffset.UtcNow);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(IdentityFixture.Configuration);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Identity:Locations:DatabasePath"] = string.Empty });
        if (configuration is not null)
        {
            builder.Configuration.AddInMemoryCollection(configuration);
        }

        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton(IdentityFixture.Tenant);
        builder.Services.AddIronApi();
        builder.Services.AddIronSecurity(builder.Configuration);
        builder.Services.AddIronDatabase(connectionString);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new IdentityModule(), new SettingsModule(), new ComplianceModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();
        services?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseIronTelemetry();
        app.UseIronApi();
        app.UseIronSecurity();
        app.UseIronIdentity();
        app.MapModules();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new UnitHost(app, time, connectionString);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? cookie = null, object? body = null, string? bearer = null, IReadOnlyDictionary<string, string>? headers = null)
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

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public Task<HttpResponseMessage> LoginAsync(string loginId, IReadOnlyDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Post, "/auth/login", body: new { login_id = loginId, password = Password }, headers: headers);

    /// <summary>The person's new session cookie, signed in with their password now.</summary>
    public async Task<string> SessionOfAsync(Principal person, IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(person);
        using var login = await LoginAsync(person.LoginId, headers);
        var session = Cookie(login, SessionCookie);
        Assert.True(session is not null, await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return session;
    }

    /// <summary>A person with a password, active, in the root unit, with their login identifier as their email.</summary>
    public async Task<Principal> PersonAsync(string? loginId = null, string? displayName = null)
    {
        loginId ??= $"person-{Ids.New():N}@acme.test";
        await using var scope = _app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<IdentityDbContext>();
        var person = new Principal(loginId, PrincipalType.User, displayName ?? $"Person {Ids.New():N}"[..15], loginId);
        person.AcceptInvitation(services.GetRequiredService<IPasswordHasher>().Hash(Password), null);
        person.SetHomeUnit((await services.GetRequiredService<OrganisationUnitService>().RootAsync(TestContext.Current.CancellationToken))!.Id);
        context.Principals.Add(person);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return person;
    }

    public async Task<Principal> PersonWithRoleAsync(string roleCode, string? loginId = null, string? displayName = null)
    {
        var person = await PersonAsync(loginId, displayName);
        await AssignAsync(person.Id, roleCode);
        return person;
    }

    /// <summary>A tenant administrator and their session, signed in now, so a change behind step-up goes through.</summary>
    public async Task<(Principal Person, string Session)> AdministratorAsync(string? displayName = null)
    {
        var person = await PersonWithRoleAsync(SystemRoles.TenantAdministrator, displayName: displayName);
        return (person, await SessionOfAsync(person));
    }

    public async Task AssignAsync(Guid principalId, string roleCode)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var assigned = await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principalId, roleCode, TestContext.Current.CancellationToken);
        Assert.True(assigned.IsSuccess, assigned.IsFailure ? assigned.Error.Message : null);
    }

    /// <summary>A role of its own with these permissions, for people.</summary>
    public async Task<string> RoleAsync(IReadOnlyList<string> permissions)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var role = new Role($"r-{Ids.New():N}"[..20], "Test role", string.Empty, isSystem: false, PrincipalKind.User, permissions);
        context.Roles.Add(role);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return role.Code;
    }

    /// <summary>A service account with this secret and the service-default role, active.</summary>
    public async Task<Principal> ServiceAccountAsync(string secret)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<IdentityDbContext>();
        var account = new Principal($"client-{Ids.New():N}", PrincipalType.ServiceAccount, "A test client", null);
        account.AcceptInvitation(services.GetRequiredService<IPasswordHasher>().Hash(secret), null);
        account.SetHomeUnit((await services.GetRequiredService<OrganisationUnitService>().RootAsync(TestContext.Current.CancellationToken))!.Id);
        context.Principals.Add(account);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await AssignAsync(account.Id, SystemRoles.ServiceDefault);
        return account;
    }

    /// <summary>An access token by the client credentials grant.</summary>
    public async Task<string> TokenAsync(Principal client, string secret)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal) { ["grant_type"] = "client_credentials", ["client_id"] = client.LoginId, ["client_secret"] = secret });
        using var response = await _client.PostAsync(new Uri("/connect/token", UriKind.Relative), form, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    public async Task SetAsync(string key, object value)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var set = await scope.ServiceProvider.GetRequiredService<SettingsWriter>().SetAsync(key, SettingPlace.Tenant, JsonSerializer.SerializeToElement(value), TestContext.Current.CancellationToken);
        Assert.True(set.IsSuccess, set.IsFailure ? set.Error.ToString() : null);
    }

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = _app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public Task<Principal> PrincipalAsync(Guid id) =>
        WithScopeAsync(services => services.GetRequiredService<IdentityDbContext>().Principals.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken));

    public Task<IReadOnlyList<Session>> SessionsAsync(Guid principalId) =>
        WithScopeAsync<IReadOnlyList<Session>>(async services => await services.GetRequiredService<IdentityDbContext>().Sessions.AsNoTracking().Where(s => s.PrincipalId == principalId).OrderBy(s => s.CreatedAt).ToListAsync(TestContext.Current.CancellationToken));

    public Task<IReadOnlyList<SecurityEvent>> SecurityEventsAsync(Guid principalId) =>
        WithScopeAsync(services => PasswordPolicyTests.SecurityEventsAsync(services, principalId));

    /// <summary>The audit events of a type, oldest first, about a target when one is named.</summary>
    public Task<IReadOnlyList<AuditEventRecord>> AuditAsync(string eventType, string? target = null) =>
        WithScopeAsync<IReadOnlyList<AuditEventRecord>>(async services => await services.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.EventType == eventType && (target == null || e.Target == target))
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .ToListAsync(TestContext.Current.CancellationToken));

    public static string? Cookie(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith($"{name}=", StringComparison.Ordinal) && v.Length > name.Length + 1)
            : null;
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(status == response.StatusCode, $"Expected {status}, got {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>The problem's error code, after checking the status.</summary>
    public static async Task<string?> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var problem = await JsonAsync(response, status);
        return problem.GetProperty("error_code").GetString();
    }
}
