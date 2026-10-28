using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Compliance;
using Iron.Kernel.Data;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Protection;
using Iron.Kernel.Protection.Contracts;
using Iron.Kernel.Protection.Services;
using Iron.Kernel.Security;
using Iron.Kernel.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// The login with the Protection module in front of it (TF-072, ADR-0053): the sign-in limit per address, and the
/// challenge after repeated failures, asked before any password, with the lockout of T2 on top. Each test speaks from
/// an address of its own, behind the ingress, so the counts of one never reach another.
/// </summary>
[Collection(IdentityCollection.Name)]
public class LoginChallengeTests(IdentityFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private WebApplication? _app;
    private HttpClient _client = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(IdentityFixture.Configuration);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Protection:Challenge:MaxNumber"] = "1000" });
        builder.Services.AddSingleton(IdentityFixture.Tenant);
        builder.Services.AddIronApi();
        builder.Services.AddIronSecurity(builder.Configuration);
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new AuthorizationModule(), new IdentityModule(), new SettingsModule(), new ComplianceModule(), new ProtectionModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

        _app = builder.Build();
        _app.UseIronApi();
        _app.UseIronSecurity();
        _app.UseIronIdentity();
        _app.UseIronProtection();
        _app.MapModules();
        await _app.StartAsync(Cancel);
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
    [Proves("TF-072 AC4", "TF-011 AC2", "ADR-0053")]
    public async Task AfterFiveFailuresFromAnAddressASignInNeedsTheChallengeBeforeAnyPasswordAndASolvedOneWorksOnce()
    {
        const string Address = "203.0.113.41";
        var loginId = $"challenge-{Ids.New():N}@acme.test";
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, loginId, Password);
        }

        for (var failure = 1; failure <= 5; failure++)
        {
            using var wrong = await LoginAsync(Address, $"nobody-{failure}-{Ids.New():N}@acme.test", "wrong", null);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var asked = await LoginAsync(Address, loginId, Password, null);
        var challenge = await ChallengeAsync(Address);
        var solved = LoginChallenges.Payload(challenge, LoginChallenges.Solve(challenge));
        using var wrongNumber = await LoginAsync(Address, loginId, Password, LoginChallenges.Payload(challenge, LoginChallenges.Solve(challenge) + 1));
        using var signedIn = await LoginAsync(Address, loginId, Password, solved);
        using var again = await LoginAsync(Address, loginId, Password, solved);
        using var elsewhere = await LoginAsync("198.51.100.41", loginId, Password, null);

        Assert.Equal((HttpStatusCode.Forbidden, IdentityErrors.ChallengeRequired), (asked.StatusCode, await ErrorCode(asked)));
        Assert.False(asked.Headers.Contains("Set-Cookie"), "Nothing is signed in before the challenge, not even with the right password.");
        Assert.Equal(IdentityErrors.ChallengeInvalid, await ErrorCode(wrongNumber));
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal((HttpStatusCode.Forbidden, IdentityErrors.ChallengeInvalid), (again.StatusCode, await ErrorCode(again)));
        Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
    }

    [Fact]
    [Proves("TF-072 AC4", "TF-012 AC2", "ADR-0053")]
    public async Task TheLockoutStillAppliesOnTopOfASolvedChallenge()
    {
        const string Address = "203.0.113.42";
        var loginId = $"locked-{Ids.New():N}@acme.test";
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, loginId, Password);
        }

        for (var failure = 1; failure <= 5; failure++)
        {
            using var wrong = await LoginAsync(Address, loginId, "wrong", null);
        }

        var challenge = await ChallengeAsync("198.51.100.42");
        using var locked = await LoginAsync("198.51.100.42", loginId, Password, LoginChallenges.Payload(challenge, LoginChallenges.Solve(challenge)));

        Assert.Equal((HttpStatusCode.Unauthorized, IdentityErrors.LoginLocked), (locked.StatusCode, await ErrorCode(locked)));
    }

    [Fact]
    [Proves("TF-072 AC1", "ADR-0053")]
    public async Task TheEleventhSignInAttemptFromAnAddressIsRefusedWhateverTheAnswersBefore()
    {
        const string Address = "203.0.113.43";
        var answers = new List<HttpStatusCode>();
        for (var attempt = 1; attempt <= 11; attempt++)
        {
            using var answer = await LoginAsync(Address, $"nobody-{Ids.New():N}@acme.test", "wrong", null);
            answers.Add(answer.StatusCode);
            if (attempt == 11)
            {
                Assert.Equal(ProtectionErrors.RateLimited, await ErrorCode(answer));
                Assert.True(answer.Headers.Contains("Retry-After"));
            }
        }

        // Five wrong passwords, then the challenge is asked; the limit counts every attempt, the company-login answer too.
        Assert.Equal([.. Enumerable.Repeat(HttpStatusCode.Unauthorized, 5), .. Enumerable.Repeat(HttpStatusCode.Forbidden, 5), HttpStatusCode.TooManyRequests], answers);
    }

    private async Task<HttpResponseMessage> LoginAsync(string address, string loginId, string password, string? challenge)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { login_id = loginId, password, challenge }) };
        request.Headers.Add("X-Forwarded-For", address);
        return await _client.SendAsync(request, Cancel);
    }

    private async Task<LoginChallenge> ChallengeAsync(string address)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/challenge");
        request.Headers.Add("X-Forwarded-For", address);
        using var answer = await _client.SendAsync(request, Cancel);
        return JsonSerializer.Deserialize<LoginChallenge>(await answer.Content.ReadAsStringAsync(Cancel), Json)!;
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
        return body.RootElement.TryGetProperty("error_code", out var code) ? code.GetString() : null;
    }
}
