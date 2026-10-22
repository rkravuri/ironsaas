using System.Net;
using System.Text.Json;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iron.Kernel.Identity.Tests;

/// <summary>The sessions console and a person's own sessions (S3 T5, TF-021 AC2, TF-022, ADR-0049).</summary>
[Collection(IdentityCollection.Name)]
public class SessionConsoleTests(IdentityFixture fixture)
{
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
    private const string Firefox = "Mozilla/5.0 (Macintosh; Intel Mac OS X 15.6; rv:143.0) Gecko/20100101 Firefox/143.0";

    [Fact]
    [Proves("TF-022 AC1", "TF-248 AC1", "ADR-0049")]
    public async Task AnAdministratorEndsASessionWithAReasonItsNextRequestIsRefusedAndTheAuditNamesWhoAndWhy()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (administrator, admin) = await host.AdministratorAsync("Ada Admin");
        var person = await host.PersonAsync();
        var session = await host.SessionOfAsync(person);
        var sessionId = (await host.SessionsAsync(person.Id)).Single().Id;

        using var withoutReason = await host.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/end", admin, new { reason = "" });
        host.Time.Advance(TimeSpan.FromMinutes(6));
        using var stale = await host.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/end", admin, new { reason = "Laptop reported stolen" });
        var fresh = await host.SessionOfAsync(administrator);
        using var ended = await host.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/end", fresh, new { reason = "Laptop reported stolen" });
        using var refused = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
        using var again = await host.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/end", fresh, new { reason = "twice" });
        var audited = Assert.Single(await host.AuditAsync(IdentityAuditEvents.SessionEnded, person.LoginId));
        var seen = (await host.SecurityEventsAsync(person.Id)).Single(e => e.Kind == SecurityEventKinds.SessionEnded);

        Assert.Equal(HttpStatusCode.BadRequest, withoutReason.StatusCode);
        Assert.Equal(RecentAuthentication.ErrorCode, await UnitHost.ProblemAsync(stale, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        Assert.Equal(PermissionErrors.SessionEnded, await UnitHost.ProblemAsync(refused, HttpStatusCode.Unauthorized));
        Assert.Equal(IdentityErrors.SessionEnded, await UnitHost.ProblemAsync(again, HttpStatusCode.Conflict));
        Assert.Equal((administrator.Id, "Laptop reported stolen", person.Id), (audited.ActorId, audited.Reason, audited.RecordId));
        Assert.Equal(sessionId.ToString("D"), JsonDocument.Parse(audited.Details!).RootElement.GetProperty("session_id").GetString());
        Assert.Equal(("Ada Admin", SecurityEventActors.Administrator, "Laptop reported stolen"), (seen.ActorName, seen.Actor, seen.Reason));
        Assert.Equal(administrator.Id, (await host.SessionsAsync(person.Id)).Single().EndedById);
    }

    [Fact]
    [Proves("TF-022 AC2")]
    public async Task SomeoneWithoutThePermissionCannotListOrEndOthersSessions()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var reader = await host.PersonWithRoleAsync(Authorization.Contracts.SystemRoles.ReadOnly);
        var session = await host.SessionOfAsync(reader);

        using var list = await host.SendAsync(HttpMethod.Get, "/api/v1/sessions", session);
        using var summary = await host.SendAsync(HttpMethod.Get, "/api/v1/sessions/summary", session);
        using var end = await host.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{Ids.New()}/end", session, new { reason = "curious" });
        using var revoke = await host.SendAsync(HttpMethod.Post, "/api/v1/sessions/revoke-tenant", session, new { reason = "curious", confirmation = "acme" });

        Assert.Equal(PermissionErrors.PermissionDenied, await UnitHost.ProblemAsync(list, HttpStatusCode.Forbidden));
        Assert.Equal(PermissionErrors.PermissionDenied, await UnitHost.ProblemAsync(summary, HttpStatusCode.Forbidden));
        Assert.Equal(PermissionErrors.PermissionDenied, await UnitHost.ProblemAsync(end, HttpStatusCode.Forbidden));
        Assert.Equal(PermissionErrors.PermissionDenied, await UnitHost.ProblemAsync(revoke, HttpStatusCode.Forbidden));
    }

    [Fact]
    [Proves("TF-022 AC3")]
    public async Task ThereAreNoSessionsToListOnceAPersonsLastOneEnded()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (_, admin) = await host.AdministratorAsync();
        var person = await host.PersonAsync();
        var session = await host.SessionOfAsync(person);
        using var logout = await host.SendAsync(HttpMethod.Post, "/auth/logout", session);

        using var list = await host.SendAsync(HttpMethod.Get, $"/api/v1/sessions?filter=principal_id:eq:{person.Id}", admin);
        var body = await UnitHost.JsonAsync(list);

        Assert.Equal(0, body.GetProperty("data").GetArrayLength());
        Assert.Equal(0, body.GetProperty("meta").GetProperty("total_count").GetInt32());
    }

    [Fact]
    [Proves("TF-022 AC4", "ADR-0049")]
    public async Task APersonSeesTheirSessionsWithDeviceAndPlaceAndEndsTheOthers()
    {
        var database = TestLocationDatabase.Write([("81.2.69.0", 24, "London", "United Kingdom"), ("89.160.20.0", 24, null, "Sweden")]);
        await using var host = await UnitHost.StartAsync(fixture, configuration: new Dictionary<string, string?>(StringComparer.Ordinal) { ["Identity:Locations:DatabasePath"] = database });
        var person = await host.PersonAsync();
        var first = await host.SessionOfAsync(person, new Dictionary<string, string> { ["User-Agent"] = Chrome, ["X-Forwarded-For"] = "81.2.69.160" });
        host.Time.Advance(TimeSpan.FromMinutes(2));
        var second = await host.SessionOfAsync(person, new Dictionary<string, string> { ["User-Agent"] = Firefox, ["X-Forwarded-For"] = "10.0.0.1, 89.160.20.112" });
        host.Time.Advance(TimeSpan.FromMinutes(2));
        var third = await host.SessionOfAsync(person);

        using var mine = await host.SendAsync(HttpMethod.Get, "/api/v1/me/sessions?sort=started_at", second);
        var rows = (await UnitHost.JsonAsync(mine)).GetProperty("data").EnumerateArray().ToList();
        var firstId = rows[0].GetProperty("id").GetGuid();
        using var current = await host.SendAsync(HttpMethod.Post, $"/api/v1/me/sessions/{rows[1].GetProperty("id").GetGuid()}/end", second);
        using var endFirst = await host.SendAsync(HttpMethod.Post, $"/api/v1/me/sessions/{firstId}/end", second);
        using var firstAfter = await host.SendAsync(HttpMethod.Get, "/api/v1/me", first);
        using var others = await host.SendAsync(HttpMethod.Post, "/api/v1/me/sessions/end-others", second);
        using var thirdAfter = await host.SendAsync(HttpMethod.Get, "/api/v1/me", third);
        using var secondAfter = await host.SendAsync(HttpMethod.Get, "/api/v1/me", second);
        var login = (await host.AuditAsync(IdentityAuditEvents.LoginSucceeded, person.LoginId))[0];

        Assert.Equal(3, rows.Count);
        Assert.Equal(("Chrome", "Windows", "London", "United Kingdom", "password", false), Row(rows[0]));
        Assert.Equal(("Firefox", "macOS", null, "Sweden", "password", true), Row(rows[1]));
        Assert.Equal((null, null, null, null, "password", false), Row(rows[2]));
        Assert.True((host.Time.GetUtcNow().AddMinutes(-4) - rows[0].GetProperty("started_at").GetDateTimeOffset()).Duration() < TimeSpan.FromMilliseconds(1)); // the database keeps microseconds
        Assert.True(rows[0].TryGetProperty("last_activity_at", out _));
        Assert.Equal(IdentityErrors.SessionCurrent, await UnitHost.ProblemAsync(current, HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.NoContent, endFirst.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, firstAfter.StatusCode);
        Assert.Equal(1, (await UnitHost.JsonAsync(others)).GetProperty("sessions_ended").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, thirdAfter.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondAfter.StatusCode);

        // The full address stays in the audit trail only; the session keeps the city and the country.
        Assert.Equal("81.2.69.160", JsonDocument.Parse(login.Details!).RootElement.GetProperty("address").GetString());
    }

    [Fact]
    [Proves("TF-022 AC5", "ADR-0049")]
    public async Task TheConsoleCountsPeopleSignedInNowAndSummarisesEachDay()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 22, 0, 0, TimeSpan.Zero));
        await using var host = await UnitHost.StartAsync(fixture, time: time);
        var administrator = await host.PersonWithRoleAsync(Authorization.Contracts.SystemRoles.TenantAdministrator);
        var anna = await host.PersonAsync();
        var ben = await host.PersonAsync();
        await host.SessionOfAsync(anna);
        await host.SessionOfAsync(anna);
        var benOne = await host.SessionOfAsync(ben);
        time.Advance(TimeSpan.FromHours(3)); // 21 October, 01:00: the sessions of the evening ran idle
        var annaToday = await host.SessionOfAsync(anna);
        var admin = await host.SessionOfAsync(administrator);
        var benToday = await host.SessionOfAsync(ben);
        var benSession = (await host.SessionsAsync(ben.Id)).Last().Id;
        time.Advance(TimeSpan.FromMinutes(1));
        using var ended = await host.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{benSession}/end", admin, new { reason = "Left the company today" });

        using var summary = await host.SendAsync(HttpMethod.Get, "/api/v1/sessions/summary?days=2", admin);
        var body = await UnitHost.JsonAsync(summary);
        var days = body.GetProperty("days").EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        Assert.Equal(2, body.GetProperty("signed_in_now").GetInt32()); // Anna and the administrator; Ben's ended
        Assert.Equal(("2026-10-20", 3, 2, 0), Day(days[0]));
        Assert.Equal(("2026-10-21", 3, 3, 1), Day(days[1]));
        Assert.NotNull(annaToday);
        Assert.NotNull(benOne);
        Assert.NotNull(benToday);
    }

    [Fact]
    [Proves("TF-022 AC5")]
    public void ThePeakCountsAPersonOnceHoweverManySessionsTheyHave()
    {
        var day = new DateOnly(2026, 10, 21);
        var nine = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
        var anna = Ids.New();
        var ben = Ids.New();
        var cleo = Ids.New();
        var intervals = new List<(Guid, DateTimeOffset, DateTimeOffset)>
        {
            (anna, nine, nine.AddHours(2)),
            (anna, nine.AddMinutes(30), nine.AddHours(3)), // overlapping: still one person
            (ben, nine.AddHours(1), nine.AddHours(4)),
            (cleo, nine.AddHours(3), nine.AddHours(5)), // starts as Anna's second ends: two at once, not three
            (cleo, nine.AddDays(-1), nine.AddDays(-1).AddHours(1)), // the day before
        };

        var peaks = SessionConsole.PeakPeoplePerDay(intervals, day.AddDays(-1), day);

        Assert.Equal(1, peaks[day.AddDays(-1)]);
        Assert.Equal(2, peaks[day]);
    }

    [Fact]
    [Proves("TF-021 AC2", "ADR-0049")]
    public async Task ASignInPastTheLimitEndsTheOldestSessionWhichHearsWhy()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        await host.SetAsync(IdentitySettings.SessionConcurrentLimit.Key, 2);
        var person = await host.PersonAsync();
        var first = await host.SessionOfAsync(person);
        host.Time.Advance(TimeSpan.FromSeconds(1));
        var second = await host.SessionOfAsync(person);
        host.Time.Advance(TimeSpan.FromSeconds(1));
        var third = await host.SessionOfAsync(person);

        using var oldest = await host.SendAsync(HttpMethod.Get, "/api/v1/me", first);
        var problem = await UnitHost.JsonAsync(oldest, HttpStatusCode.Unauthorized);
        using var middle = await host.SendAsync(HttpMethod.Get, "/api/v1/me", second);
        using var newest = await host.SendAsync(HttpMethod.Get, "/api/v1/me", third);
        var audited = Assert.Single(await host.AuditAsync(IdentityAuditEvents.SessionEnded, person.LoginId));

        Assert.Equal(PermissionErrors.SessionLimit, problem.GetProperty("error_code").GetString());
        Assert.Contains("signed in somewhere else", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, middle.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newest.StatusCode);
        Assert.Equal(SessionEndReasons.Limit, audited.Reason);
        Assert.Equal(SessionEndReasons.Limit, (await host.SessionsAsync(person.Id))[0].EndReason);
    }

    [Fact]
    [Proves("TF-021 AC2")]
    public async Task WithoutALimitEverySessionOfAPersonGoesOn()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var person = await host.PersonAsync();
        var sessions = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            sessions.Add(await host.SessionOfAsync(person));
        }

        foreach (var session in sessions)
        {
            using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }
    }

    [Theory]
    [Proves("TF-022 AC4", "ADR-0049")]
    [InlineData(Chrome, "Chrome", "Windows")]
    [InlineData(Firefox, "Firefox", "macOS")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36 Edg/141.0.0.0", "Edge", "Windows")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 Safari/604.1", "Safari", "iOS")]
    [InlineData("Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36", "Chrome", "Android")]
    [InlineData("curl/8.9.1", null, null)]
    public void ADeviceIsNamedAsAPersonKnowsIt(string agent, string? browser, string? platform)
    {
        Assert.Equal(new DeviceName(browser, platform), DeviceNames.From(agent));
    }

    [Fact]
    [Proves("TF-022 AC4", "ADR-0049")]
    public void APlaceIsFoundOnlyForAPublicAddressAndOnlyAsCityAndCountry()
    {
        var path = TestLocationDatabase.Write([("81.2.69.0", 24, "London", "United Kingdom"), ("89.160.20.0", 24, null, "Sweden")]);
        using var locations = new MmdbIpLocations(path, NullLogger<MmdbIpLocations>.Instance);
        using var none = new MmdbIpLocations(Path.Combine(Path.GetTempPath(), "missing.mmdb"), NullLogger<MmdbIpLocations>.Instance);

        Assert.Equal(new IpLocation("London", "United Kingdom"), locations.Find(IPAddress.Parse("81.2.69.160")));
        Assert.Equal(new IpLocation("London", "United Kingdom"), locations.Find(IPAddress.Parse("::ffff:81.2.69.1")));
        Assert.Equal(new IpLocation(null, "Sweden"), locations.Find(IPAddress.Parse("89.160.20.112")));
        Assert.Null(locations.Find(IPAddress.Parse("8.8.8.8")));
        Assert.Null(locations.Find(IPAddress.Parse("192.168.1.10")));
        Assert.Null(locations.Find(IPAddress.Loopback));
        Assert.Null(none.Find(IPAddress.Parse("81.2.69.160")));
    }

    [Fact]
    [Proves("ADR-0049")]
    public void TheClientsAddressIsTheLastTheIngressAddedAndOnlyBehindAPrivatePeer()
    {
        var behindIngress = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        behindIngress.Connection.RemoteIpAddress = IPAddress.Parse("172.18.0.5");
        behindIngress.Request.Headers["X-Forwarded-For"] = "203.0.113.9, 81.2.69.160";
        var direct = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        direct.Connection.RemoteIpAddress = IPAddress.Parse("81.2.69.160");
        direct.Request.Headers["X-Forwarded-For"] = "203.0.113.9";

        Assert.Equal(IPAddress.Parse("81.2.69.160"), ClientAddress.Of(behindIngress));
        Assert.Equal(IPAddress.Parse("81.2.69.160"), ClientAddress.Of(direct));
    }

    private static (string?, string?, string?, string?, string, bool) Row(JsonElement row) => (
        row.GetProperty("browser").GetString(),
        row.GetProperty("platform").GetString(),
        row.GetProperty("city").GetString(),
        row.GetProperty("country").GetString(),
        row.GetProperty("method").GetString()!,
        row.GetProperty("current").GetBoolean());

    private static (string, int, int, int) Day(JsonElement day) => (
        day.GetProperty("date").GetString()!,
        day.GetProperty("started").GetInt32(),
        day.GetProperty("peak_people").GetInt32(),
        day.GetProperty("ended_by_administrator").GetInt32());
}
