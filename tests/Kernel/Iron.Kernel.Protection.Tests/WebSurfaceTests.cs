using System.Net;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Protection.Contracts;
using Iron.Kernel.Protection.Services;
using Iron.Kernel.Security.Keys;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Protection.Tests;

public class WebSurfaceTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-070 AC3", "ADR-0053")]
    public async Task AReportOfEitherFormIsCountedKeptWithoutItsQueryAndItsEndpointCannotBeFlooded()
    {
        await using var host = await ProtectionHost.StartAsync(new Dictionary<string, string?> { ["Protection:CspReports:PerMinute"] = "3" });
        const string ReportUri = """{"csp-report":{"document-uri":"https://echo.localhost/console/users?code=secret","effective-directive":"script-src-elem","blocked-uri":"inline","script-sample":"window.injected = true","disposition":"enforce"}}""";
        const string ReportTo = """[{"type":"csp-violation","url":"https://echo.localhost/audit","body":{"documentURL":"https://echo.localhost/audit","effectiveDirective":"style-src-elem","blockedURL":"inline","sample":".x{color:red}","disposition":"enforce"}}]""";

        using var first = await PostAsync(host, ReportUri, "application/csp-report");
        using var second = await PostAsync(host, ReportTo, "application/reports+json");
        using var noReport = await PostAsync(host, """{"hello":"there"}""", "application/json");
        using var flood = await PostAsync(host, ReportUri, "application/csp-report");
        using var latest = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Get, "/api/v1/csp-reports", principal: Guid.NewGuid()), Cancel);

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests), (first.StatusCode, second.StatusCode, noReport.StatusCode, flood.StatusCode));
        using var body = JsonDocument.Parse(await latest.Content.ReadAsStringAsync(Cancel));
        var reports = body.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(["style-src-elem", "script-src-elem"], reports.Select(r => r.GetProperty("directive").GetString()));
        Assert.Equal("https://echo.localhost/console/users", reports[1].GetProperty("page").GetString());
        Assert.Equal("window.injected = true", reports[1].GetProperty("sample").GetString());
    }

    [Fact]
    [Proves("TF-070 AC2", "ADR-0053")]
    public async Task OnlyTheOriginsATenantListsMayCallItsApiFromABrowser()
    {
        await using var host = await ProtectionHost.StartAsync(new Dictionary<string, string?> { ["Security:Cors:AllowedOrigins:0"] = "https://operator.example" });
        host.Settings.Set(ProtectionSettings.AllowedOrigins, JsonSerializer.SerializeToElement(new[] { "https://portal.example.com" }));

        using var preflight = ProtectionHost.Request(HttpMethod.Options, "/api/v1/things", origin: "https://portal.example.com");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        using var allowed = await host.Client.SendAsync(preflight, Cancel);
        using var listed = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Get, "/api/v1/things", origin: "https://portal.example.com"), Cancel);
        using var other = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Get, "/api/v1/things", origin: "https://elsewhere.example"), Cancel);
        host.Settings.Set(ProtectionSettings.AllowedOrigins, JsonSerializer.SerializeToElement(Array.Empty<string>()));
        using var removed = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Get, "/api/v1/things", origin: "https://portal.example.com"), Cancel);
        using var operatorOrigin = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Get, "/api/v1/things", origin: "https://operator.example"), Cancel);

        Assert.Equal("https://portal.example.com", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("https://portal.example.com", listed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", listed.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"), "An origin the tenant does not list gets no CORS answer, so the browser refuses the page its response.");
        Assert.False(removed.Headers.Contains("Access-Control-Allow-Origin"), "A removed origin applies at the next request.");
        Assert.Equal("https://operator.example", operatorOrigin.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("X-Correlation-Id", operatorOrigin.Headers.GetValues("Access-Control-Expose-Headers").Single(), StringComparison.Ordinal);
    }

    [Theory]
    [Proves("TF-070 AC2", "ADR-0053")]
    [InlineData("""["https://portal.example.com"]""", true)]
    [InlineData("""["https://portal.example.com:8443", "http://localhost:5173", "http://app.acme.localhost"]""", true)]
    [InlineData("""["http://portal.example.com"]""", false)]
    [InlineData("""["https://portal.example.com/path"]""", false)]
    [InlineData("""["https://user@portal.example.com"]""", false)]
    [InlineData("""["*"]""", false)]
    [InlineData("\"https://portal.example.com\"", false)]
    public void AnOriginIsASchemeAndAHostWithAnOptionalPortAndHttpOnlyOnThisMachine(string json, bool allowed)
    {
        using var value = JsonDocument.Parse(json);
        Assert.Equal(allowed, ProtectionSettings.IsOriginList(value.RootElement));
    }

    [Fact]
    [Proves("TF-072 AC4", "ADR-0053")]
    public async Task AfterFiveFailuresASignInNeedsAChallengeTheServerIssuedSolvedAndUsedOnce()
    {
        await using var host = await ProtectionHost.StartAsync(new Dictionary<string, string?> { ["Protection:Challenge:MaxNumber"] = "2000" });
        var challenges = host.Services.GetRequiredService<ILoginChallenges>();
        var address = IPAddress.Parse("203.0.113.7");
        for (var failure = 1; failure <= 4; failure++)
        {
            challenges.Failed(address, $"nobody-{failure}@acme.localhost");
        }

        var beforeFifth = challenges.IsRequired(address, "ada@acme.localhost");
        challenges.Failed(address, "nobody-5@acme.localhost");
        var fromAddress = challenges.IsRequired(address, "ada@acme.localhost");
        var elsewhere = challenges.IsRequired(IPAddress.Parse("198.51.100.9"), "ada@acme.localhost");

        using var answer = await host.Client.SendAsync(ProtectionHost.Request(HttpMethod.Get, "/auth/challenge"), Cancel);
        var challenge = JsonSerializer.Deserialize<LoginChallenge>(await answer.Content.ReadAsStringAsync(Cancel), new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
        var solved = LoginChallenges.Payload(challenge, LoginChallenges.Solve(challenge));
        var wrong = LoginChallenges.Payload(challenge, LoginChallenges.Solve(challenge) + 1);
        var forged = LoginChallenges.Payload(challenge with { Signature = "local:v1:" + Convert.ToBase64String(new byte[32]) }, LoginChallenges.Solve(challenge));
        challenges.Failed(address, "nobody-6@acme.localhost");
        challenges.Failed(address, "nobody-7@acme.localhost");
        var harder = await challenges.IssueAsync(address, Cancel);

        Assert.False(beforeFifth);
        Assert.True(fromAddress);
        Assert.False(elsewhere);
        Assert.Equal(("SHA-256", 2000L), (challenge.Algorithm, challenge.MaxNumber));
        Assert.Equal(8000L, harder.MaxNumber); // two failures past the threshold: twice doubled, the progressive delay
        Assert.False(await challenges.VerifyAsync(wrong, Cancel), "A wrong number is refused.");
        Assert.False(await challenges.VerifyAsync(forged, Cancel), "A challenge the server did not sign is refused.");
        Assert.False(await challenges.VerifyAsync(null, Cancel), "No answer is refused.");
        Assert.True(await challenges.VerifyAsync(solved, Cancel));
        Assert.False(await challenges.VerifyAsync(solved, Cancel), "An answer works once.");
    }

    [Fact]
    [Proves("ADR-0054")]
    public async Task TheCheckRotatesWithTheSigningKeyAndAChallengeOfARetiredVersionIsRefused()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        using var keys = new LocalKeyService(new KeyOptions { Prefix = "tenant-check" });
        var challenges = new LoginChallenges(new ProtectionOptions { Challenge = { MaxNumber = 300 } }, time, new ProtectionMetrics(), keys);
        var before = await challenges.IssueAsync(IPAddress.Parse("198.51.100.20"), Cancel);
        var heldBack = await challenges.IssueAsync(IPAddress.Parse("198.51.100.21"), Cancel);

        await keys.RotateAsync(KeyPurposes.KeyName("tenant-check", KeyPurpose.Signing), Cancel);
        var after = await challenges.IssueAsync(IPAddress.Parse("198.51.100.22"), Cancel);
        var beforeStillChecked = await challenges.VerifyAsync(LoginChallenges.Payload(before, LoginChallenges.Solve(before)), Cancel);
        await keys.RetireAsync(KeyPurposes.KeyName("tenant-check", KeyPurpose.Signing), 2, Cancel);
        var retiredRefused = await challenges.VerifyAsync(LoginChallenges.Payload(heldBack, LoginChallenges.Solve(heldBack)), Cancel);

        Assert.Equal((1, 2), (KeyText.VersionOf(before.Signature), KeyText.VersionOf(after.Signature)));
        Assert.True(beforeStillChecked, "A challenge issued before the rotation is checked with the version that signed it.");
        Assert.False(retiredRefused, "A challenge signed with a retired version is refused.");
        Assert.True(await challenges.VerifyAsync(LoginChallenges.Payload(after, LoginChallenges.Solve(after)), Cancel));
    }

    [Fact]
    [Proves("TF-072 AC4", "ADR-0053")]
    public async Task OneIdentifierFailingFiveTimesNeedsTheChallengeFromAnyAddressUntilItSucceedsAndAnExpiredChallengeIsRefused()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        using var keys = new LocalKeyService(new KeyOptions());
        var challenges = new LoginChallenges(new ProtectionOptions { Challenge = { MaxNumber = 500 } }, time, new ProtectionMetrics(), keys);
        for (var failure = 1; failure <= 5; failure++)
        {
            challenges.Failed(IPAddress.Parse($"203.0.113.{failure}"), "ada@acme.localhost");
        }

        var required = challenges.IsRequired(IPAddress.Parse("198.51.100.9"), "ada@acme.localhost");
        var challenge = await challenges.IssueAsync(IPAddress.Parse("198.51.100.9"), Cancel);
        var answer = LoginChallenges.Payload(challenge, LoginChallenges.Solve(challenge));
        time.Advance(TimeSpan.FromMinutes(6));
        var expired = await challenges.VerifyAsync(answer, Cancel);
        challenges.Succeeded("ada@acme.localhost");
        var afterSuccess = challenges.IsRequired(IPAddress.Parse("198.51.100.9"), "ada@acme.localhost");
        time.Advance(TimeSpan.FromMinutes(16));
        var afterWindow = challenges.IsRequired(IPAddress.Parse("203.0.113.1"), "grace@acme.localhost");

        Assert.True(required);
        Assert.False(expired, "A challenge past its five minutes is refused.");
        Assert.False(afterSuccess);
        Assert.False(afterWindow, "Failures count within fifteen minutes.");
    }

    private static async Task<HttpResponseMessage> PostAsync(ProtectionHost host, string body, string contentType)
    {
        using var request = ProtectionHost.Request(HttpMethod.Post, "/api/v1/csp-reports");
        request.Content = new StringContent(body, Encoding.UTF8, contentType);
        return await host.Client.SendAsync(request, Cancel);
    }
}
