using System.Net;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Identity.Tests;

/// <summary>Logout and revocation: of a person, of a service account, of the whole tenant (S3 T5, TF-029, ADR-0049).</summary>
[Collection(IdentityCollection.Name)]
public class RevocationTests(IdentityFixture fixture)
{
    private const string Secret = "a client secret of some length";

    [Fact]
    [Proves("TF-029 AC2", "TF-029 AC4", "ADR-0049")]
    public async Task ATokenStillWithinItsFifteenMinutesIsRefusedOnceItsServiceAccountIsRevoked()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (_, admin) = await host.AdministratorAsync();
        var client = await host.ServiceAccountAsync(Secret);
        var token = await host.TokenAsync(client, Secret);
        using var before = await host.SendAsync(HttpMethod.Get, "/api/v1/roles", bearer: token);

        host.Time.Advance(TimeSpan.FromMinutes(1));
        using var revoked = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{client.Id}/revoke", admin, new { reason = "Its secret leaked into a build log" });
        using var after = await host.SendAsync(HttpMethod.Get, "/api/v1/roles", bearer: token);
        host.Time.Advance(TimeSpan.FromSeconds(1));
        var newer = await host.TokenAsync(client, Secret);
        using var withNewer = await host.SendAsync(HttpMethod.Get, "/api/v1/roles", bearer: newer);
        var seen = (await host.SecurityEventsAsync(client.Id)).Single(e => e.Kind == SecurityEventKinds.AccessRevoked);

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(PermissionErrors.TokenRevoked, await UnitHost.ProblemAsync(after, HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.OK, withNewer.StatusCode);
        Assert.Equal("Its secret leaked into a build log", seen.Reason);
    }

    [Fact]
    [Proves("TF-029 AC4", "TF-248 AC1", "ADR-0049")]
    public async Task RevokingAPersonEndsEverySessionWithAReasonAfterAFreshSignInAndTheySeeWhy()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (administrator, admin) = await host.AdministratorAsync("Ada Admin");
        var person = await host.PersonAsync();
        var phone = await host.SessionOfAsync(person);
        var laptop = await host.SessionOfAsync(person);

        using var withoutReason = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/revoke", admin, new { reason = " " });
        using var self = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{administrator.Id}/revoke", admin, new { reason = "testing" });
        host.Time.Advance(TimeSpan.FromMinutes(6));
        using var stale = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/revoke", admin, new { reason = "Phishing report" });
        var fresh = await host.SessionOfAsync(administrator);
        using var revoked = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/revoke", fresh, new { reason = "Phishing report" });
        using var phoneAfter = await host.SendAsync(HttpMethod.Get, "/api/v1/me", phone);
        using var laptopAfter = await host.SendAsync(HttpMethod.Get, "/api/v1/me", laptop);
        var again = await host.SessionOfAsync(person);
        using var signedInAgain = await host.SendAsync(HttpMethod.Get, "/api/v1/me", again);
        var seen = (await host.SecurityEventsAsync(person.Id)).Single(e => e.Kind == SecurityEventKinds.AccessRevoked);
        var audited = Assert.Single(await host.AuditAsync(SecurityEventKinds.AuditEventOf(SecurityEventKinds.AccessRevoked), person.LoginId));

        Assert.Equal(HttpStatusCode.BadRequest, withoutReason.StatusCode);
        Assert.Equal(IdentityErrors.UserSelf, await UnitHost.ProblemAsync(self, HttpStatusCode.Conflict));
        Assert.Equal(RecentAuthentication.ErrorCode, await UnitHost.ProblemAsync(stale, HttpStatusCode.Forbidden));
        Assert.Equal(2, (await UnitHost.JsonAsync(revoked)).GetProperty("sessions_ended").GetInt32());
        Assert.Equal(PermissionErrors.SessionRevoked, await UnitHost.ProblemAsync(phoneAfter, HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.Unauthorized, laptopAfter.StatusCode);
        Assert.Equal(HttpStatusCode.OK, signedInAgain.StatusCode); // revoking is not deactivating: a new sign-in works
        Assert.Equal(("Ada Admin", "Phishing report"), (seen.ActorName, seen.Reason));
        Assert.Equal((administrator.Id, "Phishing report"), (audited.ActorId, audited.Reason));
    }

    [Fact]
    [Proves("TF-029 AC1", "ADR-0049")]
    public async Task ATenantWideRevocationStopsEverySessionAndTokenAtOnceHereAndWithinAMinuteInAnotherProcess()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        await using var other = await UnitHost.StartAsync(fixture, connectionString: host.ConnectionString);
        var (administrator, admin) = await host.AdministratorAsync();
        var person = await host.PersonAsync();
        var session = await host.SessionOfAsync(person);
        var client = await host.ServiceAccountAsync(Secret);
        var token = await host.TokenAsync(client, Secret);

        // The other process has read the tenant's time already: nothing revoked yet, kept for up to a minute.
        using (var warm = await other.SendAsync(HttpMethod.Get, "/api/v1/roles", bearer: token))
        {
            Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        }

        using var mistyped = await host.SendAsync(HttpMethod.Post, "/api/v1/sessions/revoke-tenant", admin, new { reason = "Credential stuffing seen", confirmation = "acne" });
        using var revoked = await host.SendAsync(HttpMethod.Post, "/api/v1/sessions/revoke-tenant", admin, new { reason = "Credential stuffing seen", confirmation = "acme" });
        using var sessionHere = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
        using var sessionThere = await other.SendAsync(HttpMethod.Get, "/api/v1/me", admin);
        using var tokenHere = await host.SendAsync(HttpMethod.Get, "/api/v1/roles", bearer: token);
        other.Time.Advance(RevocationTimes.CacheFor + TimeSpan.FromSeconds(1));
        using var tokenThere = await other.SendAsync(HttpMethod.Get, "/api/v1/roles", bearer: token);
        var audited = Assert.Single(await host.AuditAsync(IdentityAuditEvents.TenantRevoked));

        Assert.Equal(IdentityErrors.RevocationConfirmation, await UnitHost.ProblemAsync(mistyped, HttpStatusCode.BadRequest));
        Assert.True((await UnitHost.JsonAsync(revoked)).GetProperty("sessions_ended").GetInt32() >= 2);
        Assert.Equal(PermissionErrors.SessionRevoked, await UnitHost.ProblemAsync(sessionHere, HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.Unauthorized, sessionThere.StatusCode); // the asker's own too: everyone signs in again
        Assert.Equal(PermissionErrors.TokenRevoked, await UnitHost.ProblemAsync(tokenHere, HttpStatusCode.Unauthorized));
        Assert.Equal(PermissionErrors.TokenRevoked, await UnitHost.ProblemAsync(tokenThere, HttpStatusCode.Unauthorized));
        Assert.Equal((administrator.Id, "Credential stuffing seen", "high"), (audited.ActorId, audited.Reason, audited.Severity));
    }

    [Fact]
    [Proves("TF-029 AC2", "ADR-0049")]
    public void ATokenIssuedInTheSecondOfARevocationIsRefusedAndOneIssuedAfterIsNot()
    {
        var at = new DateTimeOffset(2026, 10, 22, 12, 0, 0, 500, TimeSpan.Zero);
        var sameSecond = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero); // a token's issue time has whole seconds
        var nextSecond = sameSecond.AddSeconds(1);

        Assert.True(RevocationTimes.IssuedBefore(sameSecond.AddSeconds(-1), at));
        Assert.True(RevocationTimes.IssuedBefore(sameSecond, at));
        Assert.False(RevocationTimes.IssuedBefore(nextSecond, at));
        Assert.False(RevocationTimes.IssuedBefore(sameSecond, null));
        Assert.True(RevocationTimes.IssuedBefore(null, at));
        Assert.False(RevocationTimes.IssuedBefore(sameSecond, sameSecond)); // a revocation on a whole second: that second's tokens are after it
    }

    [Fact]
    [Proves("TF-029 AC3", "ADR-0049")]
    public async Task LogoutEndsTheSessionOnTheServerClearsTheCookieAndNoAnswerIsKeptByTheBrowser()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var person = await host.PersonAsync();
        var session = await host.SessionOfAsync(person);
        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
        using var logout = await host.SendAsync(HttpMethod.Post, "/auth/logout", session);
        using var back = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);

        Assert.Equal("no-store", me.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), c => c.StartsWith(UnitHost.SessionCookie + "=;", StringComparison.Ordinal) && c.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.Unauthorized, back.StatusCode);
        Assert.Equal("no-store", back.Headers.CacheControl?.ToString());
        Assert.Equal(SessionEndReasons.Logout, (await host.SessionsAsync(person.Id)).Single().EndReason);
    }

    [Fact]
    [Proves("ADR-0049", "ADR-0033")]
    public async Task EveryAnswerOfALiveSessionSaysWhenItEndsWithoutActivity()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var person = await host.PersonAsync();
        var session = await host.SessionOfAsync(person);
        host.Time.Advance(TimeSpan.FromMinutes(5));

        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
        var idleEnds = DateTimeOffset.Parse(me.Headers.GetValues(SessionMiddleware.IdleEndsHeader).Single(), System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(host.Time.GetUtcNow().AddMinutes(60), idleEnds);
        _ = host.Services.GetRequiredService<RevocationTimes>();
    }
}
