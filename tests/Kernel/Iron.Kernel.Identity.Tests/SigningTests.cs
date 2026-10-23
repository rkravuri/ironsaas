using System.Globalization;
using System.Net;
using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// Who may sign and with what (S3 T7, TF-063, ADR-0051): Part 11's components rule as Identity proves it, the refusals no
/// credential lifts, and an access review decision signed when the tenant's policy says so. Signing goes through the
/// endpoints a person uses: the audit-trail review, always signed, and the access review decision.
/// </summary>
[Collection(IdentityCollection.Name)]
public class SigningTests(IdentityFixture fixture)
{
    private const string ImpersonationCookie = "iron.impersonation";

    [Fact]
    [Proves("TF-063 AC1", "TF-063 AC4", "TF-219 AC3", "ADR-0051")]
    public async Task TheFirstSignatureOfASessionAsksForTheLoginIdentifierAndThePasswordAndLaterOnesForThePassword()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (administrator, session) = await host.AdministratorAsync("Ada Lovelace");

        var first = await ChallengeAsync(host, session);
        using var bare = await ReviewAsync(host, session, null);
        using var noLoginId = await ReviewAsync(host, session, new { password = UnitHost.Password });
        using var signed = await ReviewAsync(host, session, new { login_id = administrator.LoginId.ToUpperInvariant(), password = UnitHost.Password });
        var later = await ChallengeAsync(host, session);
        using var noPassword = await ReviewAsync(host, session, new { });
        using var again = await ReviewAsync(host, session, new { password = UnitHost.Password });

        Assert.True(first.GetProperty("first_in_session").GetBoolean());
        Assert.True(first.GetProperty("login_id").GetBoolean());
        Assert.True(first.GetProperty("password").GetBoolean());
        Assert.False(first.GetProperty("second_factor").GetBoolean());
        Assert.Equal(ComplianceErrors.Invalid, await UnitHost.ProblemAsync(bare, HttpStatusCode.Forbidden));
        Assert.Equal(ComplianceErrors.Invalid, await UnitHost.ProblemAsync(noLoginId, HttpStatusCode.Forbidden));
        var review = await UnitHost.JsonAsync(signed, HttpStatusCode.Created);
        Assert.Equal(administrator.Id, review.GetProperty("reviewer_id").GetGuid());
        Assert.Equal("Ada Lovelace", review.GetProperty("reviewer_name").GetString());
        var today = DateOnly.FromDateTime(host.Time.GetUtcNow().UtcDateTime);
        Assert.Equal(today.AddDays(-30), DateOnly.Parse(review.GetProperty("period_from").GetString()!, CultureInfo.InvariantCulture));
        Assert.Equal(today.AddDays(-1), DateOnly.Parse(review.GetProperty("period_to").GetString()!, CultureInfo.InvariantCulture));
        var signature = review.GetProperty("signature");
        Assert.Equal(SignatureMeanings.Reviewed, signature.GetProperty("meaning").GetString());
        Assert.Equal(SignatureMethods.Password, signature.GetProperty("method").GetString());
        Assert.Equal(administrator.LoginId, signature.GetProperty("signer_login_id").GetString());
        Assert.True(signature.GetProperty("hash_matches").GetBoolean());
        Assert.False(later.GetProperty("first_in_session").GetBoolean());
        Assert.False(later.GetProperty("login_id").GetBoolean());
        Assert.True(later.GetProperty("password").GetBoolean());
        Assert.Equal(ComplianceErrors.Invalid, await UnitHost.ProblemAsync(noPassword, HttpStatusCode.Forbidden));
        Assert.Equal(SignatureMethods.Password, (await UnitHost.JsonAsync(again, HttpStatusCode.Created)).GetProperty("signature").GetProperty("method").GetString());
        Assert.Equal(3, (await host.AuditAsync(IdentityAuditEvents.LoginFailed, administrator.LoginId)).Count(e => e.Reason is "wrong_login_id_signature" or "wrong_password_signature"));
    }

    [Fact]
    [Proves("TF-063 AC4", "ADR-0051", "ADR-0047")]
    public async Task ASecondFactorIsAskedAtTheFirstSignatureAndAtLaterOnesOnlyWhenTheTenantAsks()
    {
        await using var host = await UnitHost.StartAsync(fixture, await fixture.CreateTenantDatabaseAsync($"signing_{Ids.New():N}"));
        var (administrator, session) = await host.AdministratorAsync();
        var secret = await AddAppAsync(host, session);
        host.Time.Advance(TimeSpan.FromSeconds(30));

        var first = await ChallengeAsync(host, session);
        using var withoutCode = await ReviewAsync(host, session, new { login_id = administrator.LoginId, password = UnitHost.Password });
        host.Time.Advance(TimeSpan.FromSeconds(30));
        using var withCode = await ReviewAsync(host, session, new { login_id = administrator.LoginId, password = UnitHost.Password, code = Code(host, secret) });
        var later = await ChallengeAsync(host, session);
        using var passwordOnly = await ReviewAsync(host, session, new { password = UnitHost.Password });
        await host.SetAsync(ComplianceSettings.LaterSecondFactor.Key, true);
        var asked = await ChallengeAsync(host, session);
        using var refused = await ReviewAsync(host, session, new { password = UnitHost.Password });
        host.Time.Advance(TimeSpan.FromSeconds(30));
        using var withCodeAgain = await ReviewAsync(host, session, new { password = UnitHost.Password, code = Code(host, secret) });

        Assert.True(first.GetProperty("second_factor").GetBoolean());
        Assert.Contains("authenticator_app", first.GetProperty("methods").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(IdentityErrors.SecondFactorInvalid, await UnitHost.ProblemAsync(withoutCode, HttpStatusCode.Forbidden));
        Assert.Equal(SignatureMethods.PasswordAndApp, (await UnitHost.JsonAsync(withCode, HttpStatusCode.Created)).GetProperty("signature").GetProperty("method").GetString());
        Assert.False(later.GetProperty("second_factor").GetBoolean());
        Assert.Equal(SignatureMethods.Password, (await UnitHost.JsonAsync(passwordOnly, HttpStatusCode.Created)).GetProperty("signature").GetProperty("method").GetString());
        Assert.True(asked.GetProperty("second_factor").GetBoolean());
        Assert.Equal(IdentityErrors.SecondFactorInvalid, await UnitHost.ProblemAsync(refused, HttpStatusCode.Forbidden));
        Assert.Equal(SignatureMethods.PasswordAndApp, (await UnitHost.JsonAsync(withCodeAgain, HttpStatusCode.Created)).GetProperty("signature").GetProperty("method").GetString());
    }

    [Fact]
    [Proves("TF-063 AC4", "ADR-0051")]
    public async Task ASessionIdleBeyondTheLimitEndsAndTheNextStartsAgainAtTheFirstRule()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (administrator, session) = await host.AdministratorAsync();
        using var signed = await ReviewAsync(host, session, new { login_id = administrator.LoginId, password = UnitHost.Password });
        Assert.Equal(HttpStatusCode.Created, signed.StatusCode);
        Assert.False((await ChallengeAsync(host, session)).GetProperty("first_in_session").GetBoolean());

        host.Time.Advance(TimeSpan.FromHours(3));
        using var idle = await host.SendAsync(HttpMethod.Get, $"/api/v1/signatures/challenge?record_type={ReviewType}", session);
        var fresh = await host.SessionOfAsync(administrator);
        var restarted = await ChallengeAsync(host, fresh);
        using var passwordOnly = await ReviewAsync(host, fresh, new { password = UnitHost.Password });
        using var full = await ReviewAsync(host, fresh, new { login_id = administrator.LoginId, password = UnitHost.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, idle.StatusCode);
        Assert.True(restarted.GetProperty("first_in_session").GetBoolean());
        Assert.True(restarted.GetProperty("login_id").GetBoolean());
        Assert.Equal(ComplianceErrors.Invalid, await UnitHost.ProblemAsync(passwordOnly, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.Created, full.StatusCode);
    }

    [Fact]
    [Proves("TF-063 AC6", "TF-025 AC2", "ADR-0049", "ADR-0051")]
    public async Task NobodySignsWhileSignedInAsSomeoneElseAndTheAttemptIsRecorded()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (_, admin) = await host.AdministratorAsync("Ada Admin");
        var person = await host.PersonWithRoleAsync(SystemRoles.TenantAdministrator, displayName: "Pat Person");
        using var started = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/impersonation", admin, new { reason = "Their review does not save" });
        var both = $"{admin}; {UnitHost.Cookie(started, ImpersonationCookie)}";

        using var challenge = await host.SendAsync(HttpMethod.Get, $"/api/v1/signatures/challenge?record_type={ReviewType}", both);
        using var refused = await ReviewAsync(host, both, new { login_id = person.LoginId, password = UnitHost.Password });
        var recorded = await host.AuditAsync(ComplianceAuditEvents.SignatureRefused);

        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(ComplianceErrors.Impersonation, await UnitHost.ProblemAsync(challenge, HttpStatusCode.Forbidden));
        Assert.Equal(ComplianceErrors.Impersonation, await UnitHost.ProblemAsync(refused, HttpStatusCode.Forbidden));
        Assert.Contains(recorded, e => e.ActorId == person.Id && e.ImpersonatorId is not null && e.Reason == ComplianceErrors.Impersonation);
    }

    [Fact]
    [Proves("TF-063 AC6", "ADR-0037", "ADR-0051")]
    public async Task NobodySignsForAnotherPersonWhileActingForThem()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (administrator, admin) = await host.AdministratorAsync();
        var grace = await host.PersonAsync(displayName: "Grace Delegate");
        var graceSession = await host.SessionOfAsync(grace);
        using var delegated = await host.SendAsync(HttpMethod.Post, "/api/v1/delegations", admin, new
        {
            delegate_id = grace.Id,
            duties = new[] { "all" },
            starts_at = host.Time.GetUtcNow().AddMinutes(-1),
            ends_at = host.Time.GetUtcNow().AddMinutes(30),
            reason = "Covering the audit reviews.",
        });
        using var acting = await host.SendAsync(HttpMethod.Post, "/api/v1/me/acting-for", graceSession, new { principal_id = administrator.Id });

        using var refused = await ReviewAsync(host, graceSession, new { login_id = grace.LoginId, password = UnitHost.Password });

        Assert.Equal(HttpStatusCode.Created, delegated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, acting.StatusCode);
        Assert.Equal(ComplianceErrors.ActingFor, await UnitHost.ProblemAsync(refused, HttpStatusCode.Forbidden));
    }

    [Fact]
    [Proves("TF-063 AC6", "ADR-0051")]
    public async Task AServiceAccountNeverSignsWhateverItsRoles()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        const string secret = "a service account secret of some length";
        var client = await host.ServiceAccountAsync(secret);
        await host.AssignAsync(client.Id, SystemRoles.TenantAdministrator);
        var token = await host.TokenAsync(client, secret);

        using var refused = await host.SendAsync(HttpMethod.Post, "/api/v1/audit-reviews", body: ReviewBody(host, new { login_id = client.LoginId, password = secret }), bearer: token);

        Assert.Equal(ComplianceErrors.Token, await UnitHost.ProblemAsync(refused, HttpStatusCode.Forbidden));
        Assert.Contains(await host.AuditAsync(ComplianceAuditEvents.SignatureRefused), e => e.ActorId == client.Id && e.Reason == ComplianceErrors.Token);
    }

    [Fact]
    [Proves("TF-063 AC1", "TF-063 AC2", "ADR-0051")]
    public async Task AnAccessReviewDecisionIsSignedWhenThePolicySaysSoAndRefusedWithoutIt()
    {
        await using var host = await UnitHost.StartAsync(fixture, await fixture.CreateTenantDatabaseAsync($"signing_{Ids.New():N}"));
        var (administrator, admin) = await host.AdministratorAsync("Ada Reviewer");
        var bob = await host.PersonWithRoleAsync(SystemRoles.ReadOnly, displayName: "Bob Reader");
        var carol = await host.PersonWithRoleAsync(SystemRoles.ReadOnly, displayName: "Carol Reader");
        await host.SetAsync(ComplianceSettings.Policies.Key, new Dictionary<string, object> { ["access_review_decision"] = new { required = true } });
        var root = (await UnitHost.JsonAsync(await host.SendAsync(HttpMethod.Get, "/api/v1/me", admin))).GetProperty("home_unit").GetProperty("id").GetGuid();
        var due = DateOnly.FromDateTime(host.Time.GetUtcNow().UtcDateTime).AddDays(14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var review = (await UnitHost.JsonAsync(await host.SendAsync(HttpMethod.Post, "/api/v1/access-reviews", admin, new { name = "Quarterly", due_on = due, units = new[] { new { unit_id = root, include_children = true } } }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var items = (await UnitHost.JsonAsync(await host.SendAsync(HttpMethod.Get, $"/api/v1/access-reviews/{review}/items", admin))).GetProperty("data").EnumerateArray().ToList();
        var bobs = items.Single(i => i.GetProperty("principal_id").GetGuid() == bob.Id).GetProperty("id").GetGuid();
        var carols = items.Single(i => i.GetProperty("principal_id").GetGuid() == carol.Id).GetProperty("id").GetGuid();

        using var unsigned = await host.SendAsync(HttpMethod.Post, $"/api/v1/access-reviews/{review}/items/{bobs}/decision", admin, new { decision = "confirm" });
        using var many = await host.SendAsync(HttpMethod.Post, $"/api/v1/access-reviews/{review}/confirm", admin, new { item_ids = new[] { bobs, carols } });
        using var confirmed = await host.SendAsync(HttpMethod.Post, $"/api/v1/access-reviews/{review}/items/{bobs}/decision", admin, new { decision = "confirm", comment = "Still on the team.", signature = new { login_id = administrator.LoginId, password = UnitHost.Password } });
        using var revoked = await host.SendAsync(HttpMethod.Post, $"/api/v1/access-reviews/{review}/items/{carols}/decision", admin, new { decision = "revoke", signature = new { password = UnitHost.Password } });

        Assert.Equal(ComplianceErrors.Invalid, await UnitHost.ProblemAsync(unsigned, HttpStatusCode.Forbidden));
        Assert.Equal(ComplianceErrors.Required, await UnitHost.ProblemAsync(many, HttpStatusCode.Conflict));
        var bobsDecision = (await UnitHost.JsonAsync(confirmed)).GetProperty("decision");
        Assert.Equal(SignatureMeanings.Approved, bobsDecision.GetProperty("signature").GetProperty("meaning").GetString());
        Assert.Equal(SignatureMeanings.Rejected, (await UnitHost.JsonAsync(revoked)).GetProperty("decision").GetProperty("signature").GetProperty("meaning").GetString());

        var decisionId = bobsDecision.GetProperty("decision_id").GetGuid();
        var signatures = (await UnitHost.JsonAsync(await host.SendAsync(HttpMethod.Get, $"/api/v1/signatures?record_type=access_review_decision&record_id={decisionId}", admin))).GetProperty("data");
        var signature = Assert.Single(signatures.EnumerateArray());
        Assert.Equal("Ada Reviewer", signature.GetProperty("signer_name").GetString());
        Assert.Equal(1, signature.GetProperty("record_version").GetInt32());
        Assert.True(signature.GetProperty("hash_matches").GetBoolean());
        Assert.False(signature.GetProperty("record_changed").GetBoolean());
        Assert.Equal("Still on the team.", signature.GetProperty("comment").GetString());
        Assert.Single(await host.AuditAsync(ComplianceAuditEvents.SignatureSigned), e => e.RecordId == decisionId);
    }

    private const string ReviewType = "audit_trail_review";

    private static async Task<JsonElement> ChallengeAsync(UnitHost host, string session)
    {
        using var response = await host.SendAsync(HttpMethod.Get, $"/api/v1/signatures/challenge?record_type={ReviewType}", session);
        return await UnitHost.JsonAsync(response);
    }

    private static Task<HttpResponseMessage> ReviewAsync(UnitHost host, string session, object? signature) =>
        host.SendAsync(HttpMethod.Post, "/api/v1/audit-reviews", session, ReviewBody(host, signature));

    private static object ReviewBody(UnitHost host, object? signature) => ReviewBody(host.Time.GetUtcNow(), signature);

    /// <summary>A review of the last thirty days with no findings, signed with these credentials.</summary>
    internal static object ReviewBody(DateTimeOffset now, object? signature)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return new
        {
            period_from = today.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            period_to = today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            outcome = "no_findings",
            searches = new[] { new { label = "Sign-ins that failed", link = "/audit?event_type=identity.login_failed" } },
            signature,
        };
    }

    /// <summary>An authenticator app enrolled through the person's own pages; its secret, for codes.</summary>
    private static async Task<byte[]> AddAppAsync(UnitHost host, string session)
    {
        using var setup = await host.SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app", session);
        var secret = Totp.FromBase32((await UnitHost.JsonAsync(setup)).GetProperty("secret").GetString()!);
        using var confirmed = await host.SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app/confirm", session, new { code = Code(host, secret), name = "Phone" });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        return secret;
    }

    private static string Code(UnitHost host, byte[] secret) => Totp.Code(secret, Totp.StepAt(host.Time.GetUtcNow()));
}
