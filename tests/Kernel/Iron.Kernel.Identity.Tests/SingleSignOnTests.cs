using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Changes;
using Iron.Kernel.Compliance;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Settings;
using Iron.Kernel.Settings.Services;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Kernel.Identity.Tests;

/// <summary>The test provider and a store for the single sign-on tests: the run's under make test, or their own.</summary>
public sealed class SsoFixture : IAsyncLifetime
{
    public TestOidcProvider Provider { get; private set; } = null!;

    public TestOpenBao Store { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Provider = await TestOidcProvider.StartAsync(TestContext.Current.CancellationToken);
        Store = await TestOpenBao.StartAsync("identity-sso", TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        await Store.DisposeAsync();
    }
}

/// <summary>
/// Single sign-on with OpenID Connect over HTTP (S3 T4, ADR-0048), each test on a tenant database and a store path of its
/// own, against the test provider (navikt's mock-oauth2-server), whose login page takes the claims each sign-in proves.
/// </summary>
[Collection(IdentityCollection.Name)]
public class SingleSignOnTests(IdentityFixture fixture, SsoFixture sso) : IClassFixture<SsoFixture>
{
    private const string Password = "correct horse battery staple";
    private const string SessionCookie = "iron.session";
    private const string SignInCookie = AuthenticationChallenges.CookieName;
    private const string ClientSecret = "a client secret nobody may read back 9f2c";
    private const string Domain = "example.test";

    [Fact]
    [Proves("TF-014 AC1", "ADR-0048")]
    public async Task ANewEmployeeOfAnAllowedDomainIsCreatedWithTheDefaultRoleAndHomeUnitAndASessionStarts()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var unit = await host.UnitAsync();
        var role = await host.RoleCodeAsync([IdentityPermissions.UsersRead.Name]);
        var provider = await host.ProviderAsync(admin, body =>
        {
            body["default_role_code"] = role;
            body["default_home_unit_id"] = unit;
        });

        using var callback = await host.SignInThroughAsync(provider.Id, "new-employee", Claims($"grace@{Domain}", name: "Grace Hopper"), returnTo: "/users");
        var answer = (await JsonAsync(callback)).RootElement;
        var session = Cookie(callback, SessionCookie);
        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
        var person = await host.PrincipalAsync($"grace@{Domain}");
        var assignments = await host.AssignmentsAsync(person!.Id);
        var events = await host.SecurityEventsAsync(person.Id);
        var created = await host.AuditAsync(IdentityAuditEvents.UserCreated, person.LoginId);

        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.NotNull(session);
        Assert.Equal("/users", answer.GetProperty("return_to").GetString());
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(("Grace Hopper", PrincipalStatus.Active, unit, (string?)null), (person.DisplayName, person.Status, person.HomeUnitId, person.PasswordHash));
        var assignment = Assert.Single(assignments);
        Assert.Equal((role, RoleScopeKind.UnitWithChildren, unit, (string?)null), (assignment.RoleCode, assignment.Scope.Kind, assignment.Scope.UnitId, assignment.Source));
        Assert.Contains(events, e => e.Kind == SecurityEventKinds.SsoLinked);
        Assert.Equal("sso", JsonDocument.Parse(created!.Details!).RootElement.GetProperty("source").GetString());
    }

    [Fact]
    [Proves("TF-014 AC2", "TF-011 AC2", "ADR-0048")]
    public async Task ThePasswordSignInOfAPersonWhoSignsInOnlyThroughAProviderIsRefusedWithUseYourCompanyLogin()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(admin);
        var person = await host.PersonAsync($"ada@{Domain}");
        using var before = await host.LoginAsync(person.LoginId);

        using var marked = await host.SendAsync(HttpMethod.Put, $"/api/v1/users/{person.Id}/sso-only", admin, new { sso_only = true });
        using var refused = await host.LoginAsync(person.LoginId);
        var problem = (await JsonAsync(refused)).RootElement;
        using var wrongPassword = await host.SendAsync(HttpMethod.Post, "/auth/login", body: new { login_id = person.LoginId, password = "not the password at all" });
        using var unknown = await host.SendAsync(HttpMethod.Post, "/auth/login", body: new { login_id = $"nobody@{Domain}", password = Password });
        using var recovery = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/recovery-link", admin);
        using var cleared = await host.SendAsync(HttpMethod.Put, $"/api/v1/users/{person.Id}/sso-only", admin, new { sso_only = false });
        using var after = await host.LoginAsync(person.LoginId);
        var events = await host.SecurityEventsAsync(person.Id);

        Assert.NotNull(Cookie(before, SessionCookie));
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(IdentityErrors.UseCompanyLogin, problem.GetProperty("error_code").GetString());
        Assert.Equal("Use your company login.", problem.GetProperty("detail").GetString());
        Assert.Equal(provider.Id, problem.GetProperty("provider_id").GetGuid());
        Assert.Equal(IdentityErrors.UseCompanyLogin, await ProblemAsync(wrongPassword, HttpStatusCode.Unauthorized)); // decided before any password
        Assert.Equal(IdentityErrors.LoginInvalid, await ProblemAsync(unknown, HttpStatusCode.Unauthorized)); // an identifier nobody has keeps TF-011's answer
        Assert.Equal(IdentityErrors.RecoverySsoOnly, await ProblemAsync(recovery, HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.NotNull(Cookie(after, SessionCookie));
        Assert.Equal([SecurityEventKinds.SsoOnlySet, SecurityEventKinds.SsoOnlyCleared], events.Select(e => e.Kind).Where(k => k.StartsWith("sso_only", StringComparison.Ordinal)));
    }

    [Fact]
    [Proves("TF-014 AC2", "ADR-0048")]
    public async Task EveryoneLinkedToAProviderThatIsTheOnlyWayInIsRefusedThePasswordAndMarkingSomeoneNoProviderReachesIsRefused()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(admin, body => body["sso_only"] = true);
        var linked = await host.PersonAsync($"linked@{Domain}");
        var stranger = await host.PersonAsync("stranger@elsewhere.test");

        using var first = await host.SignInThroughAsync(provider.Id, "linked-subject", Claims(linked.LoginId));
        using var password = await host.LoginAsync(linked.LoginId);
        using var strangerPassword = await host.LoginAsync(stranger.LoginId);
        using var unreachable = await host.SendAsync(HttpMethod.Put, $"/api/v1/users/{stranger.Id}/sso-only", admin, new { sso_only = true });

        Assert.NotNull(Cookie(first, SessionCookie));
        Assert.Equal(IdentityErrors.UseCompanyLogin, await ProblemAsync(password, HttpStatusCode.Unauthorized));
        Assert.NotNull(Cookie(strangerPassword, SessionCookie));
        Assert.Equal(IdentityErrors.UserSsoUnreachable, await ProblemAsync(unreachable, HttpStatusCode.Conflict));
    }

    [Fact]
    [Proves("TF-014 AC3", "ADR-0048")]
    public async Task AnEmailOutsideTheAllowedDomainsIsRefusedAndTheAttemptIsAudited()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(admin);

        using var callback = await host.SignInThroughAsync(provider.Id, "outsider", Claims("mallory@elsewhere.test"));
        var audited = await host.AuditAsync(IdentityAuditEvents.LoginFailed, "mallory@elsewhere.test");

        Assert.Equal(IdentityErrors.SsoDomainNotAllowed, await ProblemAsync(callback, HttpStatusCode.Forbidden));
        Assert.Null(Cookie(callback, SessionCookie));
        Assert.Null(await host.PrincipalAsync("mallory@elsewhere.test"));
        Assert.Equal(("domain_not_allowed", AuditOutcome.Denied), (audited!.Reason, audited.Outcome));
        Assert.Equal("sso", JsonDocument.Parse(audited.Details!).RootElement.GetProperty("method").GetString());
    }

    [Fact]
    [Proves("TF-014 AC8", "ADR-0048")]
    public async Task AnEmailTheProviderDoesNotMarkVerifiedMatchesNobodyAndCreatesNobody()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(admin);
        var existing = await host.PersonAsync($"victim@{Domain}");

        using var match = await host.SignInThroughAsync(provider.Id, "attacker", Claims(existing.LoginId, verified: false));
        using var create = await host.SignInThroughAsync(provider.Id, "someone", Claims($"new@{Domain}", verified: false));
        var audited = await host.AuditAsync(IdentityAuditEvents.LoginFailed, existing.LoginId);

        Assert.Equal(IdentityErrors.SsoEmailUnverified, await ProblemAsync(match, HttpStatusCode.Forbidden));
        Assert.Equal(IdentityErrors.SsoEmailUnverified, await ProblemAsync(create, HttpStatusCode.Forbidden));
        Assert.Empty(await host.LinksAsync(existing.Id));
        Assert.Null(await host.PrincipalAsync($"new@{Domain}"));
        Assert.Equal("email_unverified", audited!.Reason);
    }

    [Fact]
    [Proves("ADR-0048")]
    public async Task TheStateTheNonceAndThePkceVerifierBindTheProvidersAnswerToItsOwnSignInOnce()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(admin);
        var claims = Claims($"ada@{Domain}");

        // The authorization address carries the S256 hash of a verifier the unit keeps, a state and a nonce.
        var (cookie, url) = await host.StartAsync(provider.Id);
        var query = QueryHelpers.ParseQuery(url.Query);
        var back = await TestOidcProvider.SignInAsync(url, "ada", claims, TestContext.Current.CancellationToken);
        var code = QueryHelpers.ParseQuery(back.Query)["code"].ToString();
        using var wrongState = await host.SendAsync(HttpMethod.Post, "/auth/sso/callback", cookie, new { code, state = "not-the-state" });
        using var replay = await host.SendAsync(HttpMethod.Post, "/auth/sso/callback", cookie, new { code, state = query["state"].ToString() });

        using var noCookie = await host.SendAsync(HttpMethod.Post, "/auth/sso/callback", body: new { code, state = query["state"].ToString() });
        using var otherNonce = await host.SignInThroughAsync(provider.Id, "ada", claims, tamper: u => Replace(u, "nonce", "a-nonce-of-another-sign-in"));
        using var otherVerifier = await host.SignInThroughAsync(provider.Id, "ada", claims, tamper: u => Replace(u, "code_challenge", SsoSignInService.CodeChallenge("a-verifier-the-unit-never-made-0123456789abc")));
        using var right = await host.SignInThroughAsync(provider.Id, "ada", claims);
        var reasons = await host.AuditReasonsAsync(IdentityAuditEvents.LoginFailed);

        Assert.Equal(("code", "S256"), (query["response_type"].ToString(), query["code_challenge_method"].ToString()));
        Assert.Equal(SsoSignInService.Scopes, query["scope"].ToString());
        Assert.False(string.IsNullOrEmpty(query["nonce"].ToString()));
        Assert.Equal(IdentityErrors.SsoFailed, await ProblemAsync(wrongState, HttpStatusCode.Unauthorized));
        Assert.Equal(IdentityErrors.SignInExpired, await ProblemAsync(replay, HttpStatusCode.Unauthorized)); // the challenge went with the first answer
        Assert.Equal(IdentityErrors.SignInExpired, await ProblemAsync(noCookie, HttpStatusCode.Unauthorized));
        Assert.Equal(IdentityErrors.SsoFailed, await ProblemAsync(otherNonce, HttpStatusCode.Unauthorized));
        Assert.Equal(IdentityErrors.SsoFailed, await ProblemAsync(otherVerifier, HttpStatusCode.Unauthorized));
        Assert.NotNull(Cookie(right, SessionCookie));
        Assert.Contains("state", reasons);
        Assert.Contains("id_token:wrong_nonce", reasons);
        Assert.Contains("token_exchange:invalid_grant", reasons);
    }

    [Fact]
    [Proves("TF-014 AC9", "TF-010 AC3", "ADR-0048")]
    public async Task ADeactivatedPersonIsRefusedAndNeverReCreatedOrReactivatedBySingleSignOn()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(admin);
        using var created = await host.SignInThroughAsync(provider.Id, "leaver", Claims($"leaver@{Domain}"));
        var leaver = await host.PrincipalAsync($"leaver@{Domain}");
        var password = await host.PersonAsync($"password@{Domain}");

        using var deactivated = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{leaver!.Id}/deactivate", admin);
        using var deactivatedOther = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{password.Id}/deactivate", admin);
        using var linkedAgain = await host.SignInThroughAsync(provider.Id, "leaver", Claims($"leaver@{Domain}"));
        using var byEmail = await host.SignInThroughAsync(provider.Id, "another-subject", Claims($"leaver@{Domain}"));
        using var matched = await host.SignInThroughAsync(provider.Id, "password-person", Claims(password.LoginId));
        var reasons = await host.AuditReasonsAsync(IdentityAuditEvents.LoginFailed);

        Assert.NotNull(Cookie(created, SessionCookie));
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (deactivated.StatusCode, deactivatedOther.StatusCode));
        Assert.Equal(IdentityErrors.SsoDeactivated, await ProblemAsync(linkedAgain, HttpStatusCode.Forbidden));
        Assert.Equal(IdentityErrors.SsoDeactivated, await ProblemAsync(byEmail, HttpStatusCode.Forbidden));
        Assert.Equal(IdentityErrors.SsoDeactivated, await ProblemAsync(matched, HttpStatusCode.Forbidden));
        Assert.Equal(PrincipalStatus.Disabled, (await host.PrincipalAsync(leaver.LoginId))!.Status);
        Assert.Equal(PrincipalStatus.Disabled, (await host.PrincipalAsync(password.LoginId))!.Status);
        Assert.Equal(1, await host.CountPrincipalsAsync(leaver.LoginId));
        Assert.Equal(3, reasons.Count(r => r == "deactivated"));
    }

    [Fact]
    [Proves("TF-014 AC7", "ADR-0048")]
    public async Task AnExistingPersonWhoseVerifiedEmailMatchesIsLinkedAtTheirFirstSignInAndFoundByTheProvidersSubjectAfter()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(admin, body => body["just_in_time"] = false);
        var person = await host.PersonAsync($"Ada.Lovelace@{Domain}");

        using var first = await host.SignInThroughAsync(provider.Id, "ada-subject", Claims($"ada.lovelace@{Domain}"));
        using var renamed = await host.SignInThroughAsync(provider.Id, "ada-subject", Claims($"countess@{Domain}"));
        using var nobody = await host.SignInThroughAsync(provider.Id, "nobody-subject", Claims($"nobody@{Domain}"));
        var links = await host.LinksAsync(person.Id);
        var events = await host.SecurityEventsAsync(person.Id);
        var audited = await host.AuditAsync(SecurityEventKinds.AuditEventOf(SecurityEventKinds.SsoLinked), person.LoginId);
        using var record = await host.SendAsync(HttpMethod.Get, $"/api/v1/users/{person.Id}", admin);
        var recordLinks = (await JsonAsync(record)).RootElement.GetProperty("sso_links");

        Assert.NotNull(Cookie(first, SessionCookie));
        Assert.NotNull(Cookie(renamed, SessionCookie)); // the subject, not the email, finds the person after the first time
        Assert.Equal(IdentityErrors.SsoNoAccount, await ProblemAsync(nobody, HttpStatusCode.Forbidden));
        var link = Assert.Single(links);
        Assert.Equal(("ada-subject", $"countess@{Domain}"), (link.Subject, link.Email));
        Assert.Single(events, e => e.Kind == SecurityEventKinds.SsoLinked);
        Assert.NotNull(audited);
        Assert.Equal(provider.Id, recordLinks[0].GetProperty("provider_id").GetGuid());
        Assert.Equal(1, await host.CountPrincipalsAsync($"ada.lovelace@{Domain}"));
    }

    [Fact]
    [Proves("TF-014 AC4", "ADR-0031", "ADR-0048")]
    public async Task AMappingNamesOnlyRolesItsAuthorMayGrantAndEachSignInKeepsTheMappedRolesInStep()
    {
        await using var host = await StartAsync();
        var readers = await host.RoleCodeAsync([IdentityPermissions.UsersRead.Name]);
        var managers = await host.RoleCodeAsync([IdentityPermissions.UsersManage.Name]);
        var extra = await host.RoleCodeAsync([IdentityPermissions.UsersRead.Name]);
        var author = await host.SessionOfAsync(await host.PersonWithRoleAsync($"author@{Domain}", await host.RoleCodeAsync([IdentityPermissions.SsoManage.Name, AuthorizationPermissions.AssignmentsManage.Name, IdentityPermissions.UsersRead.Name])));
        object Mapping(string group, string role) => new { group, role_code = role, unit_id = (Guid?)null, include_children = false };

        using var beyond = await host.SendAsync(HttpMethod.Post, "/api/v1/sso/providers", author, host.ProviderBody(body => body["role_mappings"] = new[] { Mapping("managers", managers) }));
        var denied = await host.AuditAsync(IdentityAuditEvents.SsoProviderCreated, null, AuditOutcome.Denied);
        var provider = await host.ProviderAsync(author, body => body["role_mappings"] = new[] { Mapping("readers", readers) });
        using var first = await host.SignInThroughAsync(provider.Id, "member", Claims($"member@{Domain}", groups: ["readers", "others"]));
        var member = await host.PrincipalAsync($"member@{Domain}");
        await host.AssignAsync(member!.Id, extra);
        var mapped = await host.AssignmentsAsync(member.Id);
        using var left = await host.SignInThroughAsync(provider.Id, "member", Claims($"member@{Domain}", groups: ["others"]));
        var after = await host.AssignmentsAsync(member.Id);

        Assert.Equal(IdentityErrors.SsoEscalation, await ProblemAsync(beyond, HttpStatusCode.Forbidden));
        Assert.Contains(IdentityPermissions.UsersManage.Name, JsonDocument.Parse(denied!.Details!).RootElement.GetProperty("missing").GetString(), StringComparison.Ordinal);
        Assert.NotNull(Cookie(first, SessionCookie));
        Assert.Equal([(readers, provider.Source), (extra, (string?)null)], mapped.OrderBy(a => a.Source is null).Select(a => (a.RoleCode, a.Source)));
        Assert.NotNull(Cookie(left, SessionCookie));
        Assert.Equal([(extra, (string?)null)], after.Select(a => (a.RoleCode, a.Source))); // the grant a person made stays
    }

    [Fact]
    [Proves("TF-014 AC5", "TF-013 AC1", "ADR-0048")]
    public async Task TheTenantTrustsAProvidersMultiFactorAssertionOrAsksForThisProductsOwnSecondFactor()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var trusting = await host.ProviderAsync(admin);
        var person = await host.PersonAsync($"factor@{Domain}");
        var secret = await host.EnrolAsync(person);
        using var linked = await host.SignInThroughAsync(trusting.Id, "factor", Claims(person.LoginId, amr: ["pwd", "mfa"]));

        using var asserted = await host.SignInThroughAsync(trusting.Id, "factor", Claims(person.LoginId, amr: ["pwd", "mfa"]));
        using var notAsserted = await host.SignInThroughAsync(trusting.Id, "factor", Claims(person.LoginId, amr: ["pwd"]));
        host.Time.Advance(TimeSpan.FromSeconds(30));
        using var second = await host.SendAsync(HttpMethod.Post, "/auth/second-factor", Cookie(notAsserted, SignInCookie), new { code = host.Code(secret) });
        using var distrust = await host.SendAsync(HttpMethod.Put, $"/api/v1/sso/providers/{trusting.Id}", admin, host.ProviderBody(body => body["trust_provider_mfa"] = false, trusting.Issuer));
        using var distrusted = await host.SignInThroughAsync(trusting.Id, "factor", Claims(person.LoginId, amr: ["pwd", "mfa"]));

        // A person the policy requires a factor of, who has none: a trusted provider's multi-factor sign-in stands for it.
        await host.SetAsync(IdentitySettings.MfaRequiredFor.Key, JsonSerializer.SerializeToElement(MfaRequirement.Everyone));
        using var trustedAgain = await host.SendAsync(HttpMethod.Put, $"/api/v1/sso/providers/{trusting.Id}", await host.AdministratorSessionAsync(enrol: true), host.ProviderBody(body => body["trust_provider_mfa"] = true, trusting.Issuer));
        using var noFactor = await host.SignInThroughAsync(trusting.Id, "no-factor", Claims($"nofactor@{Domain}", amr: ["mfa"]));
        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", Cookie(noFactor, SessionCookie));
        using var noClaim = await host.SignInThroughAsync(trusting.Id, "no-factor", Claims($"nofactor@{Domain}"));

        Assert.NotNull(Cookie(linked, SessionCookie));
        Assert.NotNull(Cookie(asserted, SessionCookie));
        Assert.Equal(Session.ProviderMultiFactor, await host.SecondFactorOfAsync(Cookie(asserted, SessionCookie)!));
        Assert.Equal("second_factor", (await JsonAsync(notAsserted)).RootElement.GetProperty("step").GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(trusting.Id, await host.ProviderOfAsync(Cookie(second, SessionCookie)!)); // the second step carried the provider into the session
        Assert.Equal(HttpStatusCode.OK, distrust.StatusCode);
        Assert.Equal("second_factor", (await JsonAsync(distrusted)).RootElement.GetProperty("step").GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.OK, trustedAgain.StatusCode);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.False((await JsonAsync(me)).RootElement.GetProperty("mfa_enrolment_required").GetBoolean());
        Assert.Equal("enrolment", (await JsonAsync(noClaim)).RootElement.GetProperty("step").GetProperty("kind").GetString()); // without the claim, the policy applies
    }

    [Fact]
    [Proves("ADR-0048", "ADR-0047")]
    public async Task ProvidersAreManagedBehindTheCriticalPermissionAndARecentSignInAndTheClientSecretLivesOnlyInTheStore()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var reader = await host.SessionOfAsync(await host.PersonWithRoleAsync($"reader@{Domain}", await host.RoleCodeAsync([IdentityPermissions.UsersRead.Name])));

        using var forbidden = await host.SendAsync(HttpMethod.Get, "/api/v1/sso/providers", reader);
        host.Time.Advance(TimeSpan.FromMinutes(6));
        using var stale = await host.SendAsync(HttpMethod.Post, "/api/v1/sso/providers", admin, host.ProviderBody());
        var fresh = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(fresh);
        var stored = await host.StoredSecretAsync(provider.Id);
        using var kept = await host.SendAsync(HttpMethod.Put, $"/api/v1/sso/providers/{provider.Id}", fresh, host.ProviderBody(body => { body["client_secret"] = null; body["name"] = "Renamed"; }, provider.Issuer));
        var keptSecret = await host.StoredSecretAsync(provider.Id);
        using var replaced = await host.SendAsync(HttpMethod.Put, $"/api/v1/sso/providers/{provider.Id}", fresh, host.ProviderBody(body => body["client_secret"] = "a new secret", provider.Issuer));
        using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/sso/providers", fresh);
        var leaks = await host.TextsHoldingAsync(ClientSecret);
        var changed = await host.AuditAsync(IdentityAuditEvents.SsoProviderChanged, "Renamed");
        using var deleted = await host.SendAsync(HttpMethod.Delete, $"/api/v1/sso/providers/{provider.Id}", fresh);
        var afterDelete = await host.StoredSecretAsync(provider.Id);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("auth.reauthentication_required", await ProblemAsync(stale, HttpStatusCode.Forbidden));
        Assert.Equal(ClientSecret, stored);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(ClientSecret, keptSecret);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.DoesNotContain("a new secret", await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Empty(leaks); // no row of the unit's database holds it: providers, audit events, nothing
        Assert.Empty(await host.TextsHoldingAsync("a new secret"));
        Assert.Equal("kept", JsonDocument.Parse(changed!.Details!).RootElement.GetProperty("client_secret").GetString());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(afterDelete);
    }

    [Fact]
    [Proves("TF-014 AC6", "ADR-0048")]
    public async Task TestConnectionReadsTheDiscoveryDocumentAndTheKeysAndSaysWhatWorksAndWhatDoesNot()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();

        using var good = await host.SendAsync(HttpMethod.Post, "/api/v1/sso/test", admin, new { discovery_url = sso.Provider.DiscoveryUrl($"t{Ids.New():N}") });
        using var plainHttp = await host.SendAsync(HttpMethod.Post, "/api/v1/sso/test", admin, new { discovery_url = "http://login.example.test/.well-known/openid-configuration" });
        using var nothingThere = await host.SendAsync(HttpMethod.Post, "/api/v1/sso/test", admin, new { discovery_url = "https://127.0.0.1:1/.well-known/openid-configuration" });
        var goodBody = (await JsonAsync(good)).RootElement;

        Assert.True(goodBody.GetProperty("passed").GetBoolean());
        var checks = goodBody.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("check").GetString()!, i => i.GetProperty("status").GetString());
        Assert.Equal(SsoTestStatus.Passed, checks["discovery"]);
        Assert.Equal(SsoTestStatus.Passed, checks["keys"]);
        Assert.Equal(SsoTestStatus.Passed, checks["pkce"]);
        Assert.False((await JsonAsync(plainHttp)).RootElement.GetProperty("passed").GetBoolean());
        var unreachable = (await JsonAsync(nothingThere)).RootElement;
        Assert.False(unreachable.GetProperty("passed").GetBoolean());
        Assert.Contains("could not be reached", unreachable.GetProperty("items")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Proves("TF-014 AC1", "TF-011 AC2", "ADR-0048")]
    public async Task AnEmailOfAProvidersDomainIsSentToItAndTheAnswerIsTheSameWhetherOrNotTheAccountExists()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(admin);
        var person = await host.PersonAsync($"known@{Domain}");

        using var options = await host.SendAsync(HttpMethod.Get, "/auth/sign-in-options");
        using var known = await host.SendAsync(HttpMethod.Post, "/auth/sso/discover", body: new { email = person.LoginId });
        using var unknown = await host.SendAsync(HttpMethod.Post, "/auth/sso/discover", body: new { email = $"unknown@{Domain.ToUpperInvariant()}" });
        using var elsewhere = await host.SendAsync(HttpMethod.Post, "/auth/sso/discover", body: new { email = "someone@elsewhere.test" });

        Assert.Equal(provider.Id, (await JsonAsync(options)).RootElement.GetProperty("providers")[0].GetProperty("id").GetGuid());
        Assert.Equal(await known.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(provider.Id, (await JsonAsync(known)).RootElement.GetProperty("provider").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(elsewhere)).RootElement.GetProperty("provider").ValueKind);
    }

    [Fact]
    [Proves("ADR-0048", "TF-248 AC2")]
    public async Task ASessionKnowsItsProviderToOfferItsSignOutAndAPersonWithoutAPasswordConfirmsThroughIt()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(admin);
        using var signedIn = await host.SignInThroughAsync(provider.Id, "no-password", Claims($"nopassword@{Domain}"));
        var session = Cookie(signedIn, SessionCookie);

        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
        using var signOut = await host.SendAsync(HttpMethod.Get, $"/auth/sso/{provider.Id}/sign-out");
        using var reauthentication = await host.SendAsync(HttpMethod.Post, "/api/v1/me/reauthentication", session);
        var options = (await JsonAsync(reauthentication)).RootElement;
        var url = new Uri((await JsonAsync(signOut)).RootElement.GetProperty("url").GetString()!);

        Assert.Equal(provider.Id, (await JsonAsync(me)).RootElement.GetProperty("signed_in_with").GetProperty("provider_id").GetGuid());
        Assert.Equal($"{sso.Provider.Issuer(provider.Issuer)}/endsession", url.GetLeftPart(UriPartial.Path));
        Assert.Equal("https://acme.localhost/login", QueryHelpers.ParseQuery(url.Query)["post_logout_redirect_uri"].ToString());
        Assert.False(options.GetProperty("password").GetBoolean());
        Assert.Equal(provider.Id, options.GetProperty("provider").GetProperty("id").GetGuid());
    }

    [Fact]
    [Proves("TF-248 AC6", "ADR-0048")]
    public async Task AStepUpThroughTheProviderAsksForAFreshSignInAndCountsWhenItsAuthTimeFallsWithinTheWindow()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(admin);
        using var signedIn = await host.SignInThroughAsync(provider.Id, "no-password", Claims($"nopassword@{Domain}"));
        var session = Cookie(signedIn, SessionCookie)!;
        host.Time.Advance(TimeSpan.FromMinutes(6));

        using var refused = await host.SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app", session);
        var (signIn, url) = await host.StartReauthenticationAsync(session, new { return_to = "/me?tab=security" });
        var query = QueryHelpers.ParseQuery(url.Query);
        using var confirmed = await host.ConfirmThroughAsync(session, signIn, url, "no-password", Claims($"nopassword@{Domain}", authTime: host.Time.GetUtcNow()));
        var answer = (await JsonAsync(confirmed)).RootElement;
        using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app", session);
        var audited = await host.AuditAsync(IdentityAuditEvents.Reauthenticated, $"nopassword@{Domain}");

        Assert.Equal("auth.reauthentication_required", await ProblemAsync(refused, HttpStatusCode.Forbidden));
        Assert.Equal(("login", "0"), (query["prompt"].ToString(), query["max_age"].ToString()));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.True(answer.GetProperty("reauthenticated").GetBoolean());
        Assert.Equal("/me?tab=security", answer.GetProperty("return_to").GetString());
        Assert.Null(Cookie(confirmed, SessionCookie)); // the same session, now recent: no new one
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("sso", JsonDocument.Parse(audited!.Details!).RootElement.GetProperty("method").GetString());
    }

    [Fact]
    [Proves("TF-248 AC6", "ADR-0048")]
    public async Task AStepUpThroughAProviderThatIgnoresTheFreshSignInFailsSaysSoAndTheActionStaysRefused()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(admin);
        using var signedIn = await host.SignInThroughAsync(provider.Id, "keeps-session", Claims($"keeps@{Domain}"));
        var session = Cookie(signedIn, SessionCookie)!;
        host.Time.Advance(TimeSpan.FromMinutes(6));

        // The provider answers from its own session of an hour ago, or says nothing of when, or for another account.
        var (staleCookie, staleUrl) = await host.StartReauthenticationAsync(session, new { return_to = "/me" });
        using var stale = await host.ConfirmThroughAsync(session, staleCookie, staleUrl, "keeps-session", Claims($"keeps@{Domain}", authTime: host.Time.GetUtcNow().AddHours(-1)));
        var staleProblem = (await JsonAsync(stale)).RootElement;
        var (silentCookie, silentUrl) = await host.StartReauthenticationAsync(session, new { return_to = "/me" });
        using var silent = await host.ConfirmThroughAsync(session, silentCookie, silentUrl, "keeps-session", Claims($"keeps@{Domain}"));
        var (otherCookie, otherUrl) = await host.StartReauthenticationAsync(session, new { return_to = "/me" });
        using var other = await host.ConfirmThroughAsync(session, otherCookie, otherUrl, "someone-else", Claims($"someone@{Domain}", authTime: host.Time.GetUtcNow()));
        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
        using var action = await host.SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app", session);
        var reasons = await host.AuditReasonsAsync(IdentityAuditEvents.LoginFailed);

        Assert.Equal(IdentityErrors.SsoReauthenticationStale, await ProblemAsync(stale, HttpStatusCode.Forbidden));
        Assert.Contains("did not ask you to sign in again", staleProblem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal("/me", staleProblem.GetProperty("return_to").GetString());
        Assert.Equal(IdentityErrors.SsoReauthenticationStale, await ProblemAsync(silent, HttpStatusCode.Forbidden));
        Assert.Equal(IdentityErrors.SsoFailed, await ProblemAsync(other, HttpStatusCode.Forbidden)); // a 403: the session stays
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("auth.reauthentication_required", await ProblemAsync(action, HttpStatusCode.Forbidden));
        Assert.Contains("reauthentication_auth_time_stale", reasons);
        Assert.Contains("reauthentication_no_auth_time", reasons);
        Assert.Contains("reauthentication_other_person", reasons);
    }

    [Fact]
    [Proves("TF-248 AC2", "TF-248 AC6", "ADR-0048")]
    public async Task APersonWithASecondFactorHereGivesItBeforeConfirmingThroughTheProvider()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(admin);
        using var signedIn = await host.SignInThroughAsync(provider.Id, "with-factor", Claims($"withfactor@{Domain}"));
        var session = Cookie(signedIn, SessionCookie)!;
        var secret = await host.AddAppAsync(session);
        host.Time.Advance(TimeSpan.FromMinutes(6));

        using var withoutCode = await host.SendAsync(HttpMethod.Post, "/api/v1/me/reauthentication/sso", session, new { return_to = "/me" });
        host.Time.Advance(TimeSpan.FromSeconds(30));
        var (signIn, url) = await host.StartReauthenticationAsync(session, new { return_to = "/me", code = host.Code(secret) });
        using var confirmed = await host.ConfirmThroughAsync(session, signIn, url, "with-factor", Claims($"withfactor@{Domain}", authTime: host.Time.GetUtcNow()));
        var audited = await host.AuditAsync(IdentityAuditEvents.Reauthenticated, $"withfactor@{Domain}");

        Assert.Equal(IdentityErrors.SecondFactorInvalid, await ProblemAsync(withoutCode, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(FactorKinds.AuthenticatorApp, JsonDocument.Parse(audited!.Details!).RootElement.GetProperty("second_factor").GetString());
    }

    [Fact]
    [Proves("TF-063 AC5", "TF-248 AC6", "ADR-0048", "ADR-0051")]
    public async Task ACompanyLoginPersonSignsOnlyAfterAFreshSignInThereAndEachSignatureTakesItsOwn()
    {
        await using var host = await StartAsync();
        var admin = await host.AdministratorSessionAsync();
        var provider = await host.ProviderAsync(admin);
        using var signedIn = await host.SignInThroughAsync(provider.Id, "signer", Claims($"signer@{Domain}", name: "Sam Signer"));
        var session = Cookie(signedIn, SessionCookie)!;
        await host.AssignAsync((await host.PrincipalAsync($"signer@{Domain}"))!.Id, SystemRoles.TenantAdministrator);

        using var challenge = await host.SendAsync(HttpMethod.Get, "/api/v1/signatures/challenge?record_type=audit_trail_review", session);
        using var withoutSignIn = await host.SendAsync(HttpMethod.Post, "/api/v1/audit-reviews", session, SigningTests.ReviewBody(host.Time.GetUtcNow(), new { }));
        var (signIn, url) = await host.StartReauthenticationAsync(session, new { return_to = "/compliance/audit-reviews" });
        using var confirmed = await host.ConfirmThroughAsync(session, signIn, url, "signer", Claims($"signer@{Domain}", authTime: host.Time.GetUtcNow()));
        using var ready = await host.SendAsync(HttpMethod.Get, "/api/v1/signatures/challenge?record_type=audit_trail_review", session);
        using var signed = await host.SendAsync(HttpMethod.Post, "/api/v1/audit-reviews", session, SigningTests.ReviewBody(host.Time.GetUtcNow(), new { }));
        using var second = await host.SendAsync(HttpMethod.Post, "/api/v1/audit-reviews", session, SigningTests.ReviewBody(host.Time.GetUtcNow(), new { }));
        var (staleSignIn, staleUrl) = await host.StartReauthenticationAsync(session, new { return_to = "/compliance/audit-reviews" });
        using var staleConfirmed = await host.ConfirmThroughAsync(session, staleSignIn, staleUrl, "signer", Claims($"signer@{Domain}", authTime: host.Time.GetUtcNow()));
        host.Time.Advance(TimeSpan.FromMinutes(6));
        using var stale = await host.SendAsync(HttpMethod.Post, "/api/v1/audit-reviews", session, SigningTests.ReviewBody(host.Time.GetUtcNow(), new { }));

        var asked = (await JsonAsync(challenge)).RootElement;
        Assert.False(asked.GetProperty("password").GetBoolean());
        Assert.Equal(provider.Id, asked.GetProperty("provider_id").GetGuid());
        Assert.False(asked.GetProperty("provider_confirmed").GetBoolean());
        Assert.Equal(ComplianceErrors.ProviderSignInRequired, await ProblemAsync(withoutSignIn, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.True((await JsonAsync(ready)).RootElement.GetProperty("provider_confirmed").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, signed.StatusCode);
        var signature = (await JsonAsync(signed)).RootElement.GetProperty("signature");
        Assert.Equal(SignatureMethods.CompanyLogin, signature.GetProperty("method").GetString());
        Assert.Equal("Sam Signer", signature.GetProperty("signer_name").GetString());
        Assert.Equal(ComplianceErrors.ProviderSignInRequired, await ProblemAsync(second, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.OK, staleConfirmed.StatusCode);
        Assert.Equal(ComplianceErrors.ProviderSignInRequired, await ProblemAsync(stale, HttpStatusCode.Forbidden));
    }

    [Fact]
    [Proves("TF-068 AC1", "TF-068 AC3", "ADR-0048", "ADR-0052")]
    public async Task WithFourEyesOnForSingleSignOnANewProviderWaitsItsSecretStagedInTheStoreOnlyAndAppliesWhenAnotherAdministratorApproves()
    {
        await using var host = await StartAsync();
        var requester = await host.AdministratorSessionAsync();
        var approver = await host.AdministratorSessionAsync();
        await host.SetAsync(Iron.Kernel.Changes.Contracts.ChangesSettings.Areas.Key, JsonSerializer.SerializeToElement(new[] { ConfigurationAreas.SingleSignOn }));
        var body = host.ProviderBody();

        using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/sso/providers", requester, body);
        var held = (await JsonAsync(created)).RootElement;
        var changeId = held.GetProperty("change_id").GetGuid();
        var waiting = await host.ProviderNamedAsync((string)body["name"]!);
        var leaks = await host.TextsHoldingAsync(ClientSecret);
        using var own = await host.SendAsync(HttpMethod.Post, $"/api/v1/changes/{changeId}/approve", requester, new { reason = "Mine." });
        using var approved = await host.SendAsync(HttpMethod.Post, $"/api/v1/changes/{changeId}/approve", approver, new { reason = "Checked with the identity team." });
        var provider = await host.ProviderNamedAsync((string)body["name"]!);

        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        Assert.Equal(Iron.Kernel.Changes.Contracts.ChangesErrors.Held, held.GetProperty("error_code").GetString());
        Assert.Null(waiting);
        Assert.Empty(leaks);
        Assert.Equal(Iron.Kernel.Changes.Contracts.ChangesErrors.Own, await ProblemAsync(own, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.NotNull(provider);
        Assert.Equal(ClientSecret, await host.StoredSecretAsync(provider.Id));
        Assert.NotNull(await host.AuditAsync(Iron.Kernel.Changes.Contracts.ChangesAuditEvents.ChangeApproved, $"Add the company login {body["name"]}"));
    }

    private static Dictionary<string, object?> Claims(string email, bool verified = true, string? name = null, IReadOnlyList<string>? groups = null, IReadOnlyList<string>? amr = null, DateTimeOffset? authTime = null)
    {
        var claims = new Dictionary<string, object?>(StringComparer.Ordinal) { ["email"] = email, ["email_verified"] = verified, ["name"] = name ?? email };
        if (authTime is { } at)
        {
            claims["auth_time"] = at.ToUnixTimeSeconds();
        }

        if (groups is not null)
        {
            claims["groups"] = groups;
        }

        if (amr is not null)
        {
            claims["amr"] = amr;
        }

        return claims;
    }

    private static Uri Replace(Uri url, string name, string value)
    {
        var query = QueryHelpers.ParseQuery(url.Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
        query[name] = value;
        return new Uri(QueryHelpers.AddQueryString(url.GetLeftPart(UriPartial.Path), query!));
    }

    private async Task<TestHost> StartAsync()
    {
        var connectionString = await fixture.CreateTenantDatabaseAsync($"sso_{Ids.New():N}");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var storePath = $"tenants/sso-{Ids.New():N}";
        builder.Configuration.AddInMemoryCollection(IdentityFixture.Configuration);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Secrets:Address"] = sso.Store.Address,
            ["Secrets:Token"] = sso.Store.RootToken,
            ["Secrets:Mount"] = sso.Store.Mount,
            ["Secrets:Path"] = storePath,
        });
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton(IdentityFixture.Tenant);
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(connectionString);

        // The store holds the providers' client secrets; the keys stay the process's, as in the other Identity tests.
        builder.Services.AddIronKeys(new ConfigurationBuilder().AddInMemoryCollection(IdentityFixture.Configuration).Build());
        builder.Services.AddIronSecretStore(builder.Configuration);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new AuthorizationModule(), new IdentityModule(), new SettingsModule(), new ComplianceModule(), new ChangesModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

        var app = builder.Build();
        app.UseIronTelemetry();
        app.UseIronApi();
        app.UseIronIdentity();
        app.MapModules();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new TestHost(app, app.GetTestClient(), time, connectionString, sso, storePath);
    }

    private static string? Cookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith($"{name}=", StringComparison.Ordinal) && v.Length > name.Length + 1)
            : null;

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static async Task<string?> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(status == response.StatusCode, $"Expected {status}, got {response.StatusCode}: {body}");
        using var problem = JsonDocument.Parse(body);
        return problem.RootElement.GetProperty("error_code").GetString();
    }

    /// <summary>An assignment as the tests read it: the role's code, the scope and the source.</summary>
    private sealed record Held(string RoleCode, RoleScope Scope, string? Source);

    /// <summary>A provider the tests made: its id, its issuer's name on the test provider, and the source its mapping gives.</summary>
    private sealed record Made(Guid Id, string Issuer)
    {
        public string Source => SsoProvider.SourceOf(Id);
    }

    /// <summary>One in-memory unit on a tenant database and a store path of its own, with its clock, and the steps the tests repeat.</summary>
    private sealed class TestHost(WebApplication app, HttpClient client, FakeTimeProvider time, string connectionString, SsoFixture sso, string storePath) : IAsyncDisposable
    {
        public FakeTimeProvider Time => time;

        public string Code(byte[] secret) => Totp.Code(secret, Totp.StepAt(time.GetUtcNow()));

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? cookie = null, object? body = null)
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

            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public Task<HttpResponseMessage> LoginAsync(string loginId) => SendAsync(HttpMethod.Post, "/auth/login", body: new { login_id = loginId, password = Password });

        /// <summary>
        /// A sign-in through the provider started: the sign-in cookie and the authorization address. The test provider is
        /// one container for the whole run, which under make test's load can take longer than discovery's ten seconds;
        /// the product rightly answers identity.sso.unavailable then, so the start is tried again while it says so, up
        /// to five times, and any other answer fails at once.
        /// </summary>
        public async Task<(string Cookie, Uri Url)> StartAsync(Guid providerId, string? returnTo = null)
        {
            for (var attempt = 1; ; attempt++)
            {
                using var start = await SendAsync(HttpMethod.Post, "/auth/sso/start", body: new { provider_id = providerId, return_to = returnTo });
                if (start.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 5)
                {
                    using var problem = await JsonAsync(start);
                    Assert.Equal(IdentityErrors.SsoUnavailable, problem.RootElement.GetProperty("error_code").GetString());
                    await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                    continue;
                }

                Assert.True(start.StatusCode == HttpStatusCode.OK, $"the start answered {(int)start.StatusCode}: {await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
                return (Cookie(start, SignInCookie)!, new Uri((await JsonAsync(start)).RootElement.GetProperty("authorization_url").GetString()!));
            }
        }

        /// <summary>A whole sign-in through the provider, as the browser and the shell's callback page make it; the callback's answer.</summary>
        public async Task<HttpResponseMessage> SignInThroughAsync(Guid providerId, string subject, Dictionary<string, object?> claims, string? returnTo = null, Func<Uri, Uri>? tamper = null)
        {
            var (cookie, url) = await StartAsync(providerId, returnTo);
            var back = await TestOidcProvider.SignInAsync(tamper?.Invoke(url) ?? url, subject, claims, TestContext.Current.CancellationToken);
            var query = QueryHelpers.ParseQuery(back.Query);
            return await SendAsync(HttpMethod.Post, "/auth/sso/callback", cookie, new { code = query["code"].ToString(), state = query["state"].ToString() });
        }

        /// <summary>A re-authentication through the provider started from a session: the sign-in cookie and the authorization address.</summary>
        public async Task<(string Cookie, Uri Url)> StartReauthenticationAsync(string session, object body)
        {
            using var start = await SendAsync(HttpMethod.Post, "/api/v1/me/reauthentication/sso", session, body);
            var text = await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(start.StatusCode == HttpStatusCode.OK, text);
            return (Cookie(start, SignInCookie)!, new Uri(JsonDocument.Parse(text).RootElement.GetProperty("authorization_url").GetString()!));
        }

        /// <summary>The provider's page with this sign-in's claims, and its answer posted back from the session's browser, both cookies with it.</summary>
        public async Task<HttpResponseMessage> ConfirmThroughAsync(string session, string signIn, Uri url, string subject, Dictionary<string, object?> claims)
        {
            var back = await TestOidcProvider.SignInAsync(url, subject, claims, TestContext.Current.CancellationToken);
            var query = QueryHelpers.ParseQuery(back.Query);
            return await SendAsync(HttpMethod.Post, "/auth/sso/callback", $"{signIn}; {session}", new { code = query["code"].ToString(), state = query["state"].ToString() });
        }

        /// <summary>Adds an authenticator app from My profile, in a session that is recent; answers its secret.</summary>
        public async Task<byte[]> AddAppAsync(string session)
        {
            using var setup = await SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app", session);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            var secret = Totp.FromBase32((await JsonAsync(setup)).RootElement.GetProperty("secret").GetString()!);
            using var confirmed = await SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app/confirm", session, new { code = Code(secret), name = "Phone" });
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            time.Advance(TimeSpan.FromSeconds(30));
            return secret;
        }

        /// <summary>The body of a provider on the test provider, under an issuer of the test's own unless one is given.</summary>
        public Dictionary<string, object?> ProviderBody(Action<Dictionary<string, object?>>? change = null, string? issuer = null)
        {
            issuer ??= $"t{Ids.New():N}";
            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = $"Test provider {issuer[..9]}",
                ["preset"] = SsoPresets.Other,
                ["discovery_url"] = sso.Provider.DiscoveryUrl(issuer),
                ["client_id"] = "iron-tests",
                ["client_secret"] = ClientSecret,
                ["allowed_domains"] = new[] { Domain },
                ["just_in_time"] = true,
                ["default_role_code"] = null,
                ["default_home_unit_id"] = null,
                ["sso_only"] = false,
                ["trust_provider_mfa"] = true,
                ["role_mappings"] = Array.Empty<object>(),
            };
            change?.Invoke(body);
            return body;
        }

        public async Task<Made> ProviderAsync(string session, Action<Dictionary<string, object?>>? change = null)
        {
            var issuer = $"t{Ids.New():N}";
            using var created = await SendAsync(HttpMethod.Post, "/api/v1/sso/providers", session, ProviderBody(change, issuer));
            var body = await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(created.StatusCode == HttpStatusCode.Created, body);
            return new Made(JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid(), issuer);
        }

        /// <summary>A tenant administrator signed in now, so a change behind step-up goes through; with <paramref name="enrol"/>, enrolled first.</summary>
        public async Task<string> AdministratorSessionAsync(bool enrol = false)
        {
            var person = await PersonAsync($"admin-{Ids.New():N}@acme.test");
            await using (var scope = app.Services.CreateAsyncScope())
            {
                Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(person.Id, SystemRoles.TenantAdministrator, TestContext.Current.CancellationToken)).IsSuccess);
            }

            if (!enrol)
            {
                return await SessionOfAsync(person);
            }

            using var login = await LoginAsync(person.LoginId);
            var signIn = Cookie(login, SignInCookie)!;
            using var setup = await SendAsync(HttpMethod.Post, "/auth/enrolment/authenticator-app", signIn);
            var secret = Totp.FromBase32((await JsonAsync(setup)).RootElement.GetProperty("secret").GetString()!);
            using var enrolled = await SendAsync(HttpMethod.Post, "/auth/enrolment/authenticator-app/confirm", signIn, new { code = Code(secret) });
            return Cookie(enrolled, SessionCookie)!;
        }

        public async Task<string> SessionOfAsync(Principal person)
        {
            using var login = await LoginAsync(person.LoginId);
            var session = Cookie(login, SessionCookie);
            Assert.True(session is not null, await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            return session;
        }

        /// <summary>A person with a password, in the root unit, and their login identifier as their email.</summary>
        public async Task<Principal> PersonAsync(string loginId)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var context = services.GetRequiredService<IdentityDbContext>();
            var person = new Principal(loginId, PrincipalType.User, $"Person {Ids.New():N}"[..15], loginId);
            person.AcceptInvitation(services.GetRequiredService<IPasswordHasher>().Hash(Password), null);
            person.SetHomeUnit((await services.GetRequiredService<OrganisationUnitService>().RootAsync(TestContext.Current.CancellationToken))!.Id);
            context.Principals.Add(person);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return person;
        }

        public async Task<Principal> PersonWithRoleAsync(string loginId, string roleCode)
        {
            var person = await PersonAsync(loginId);
            await AssignAsync(person.Id, roleCode);
            return person;
        }

        public async Task AssignAsync(Guid principalId, string roleCode)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var assigned = await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principalId, roleCode, TestContext.Current.CancellationToken);
            Assert.True(assigned.IsSuccess, assigned.IsFailure ? assigned.Error.Message : null);
        }

        public async Task<string> RoleCodeAsync(IReadOnlyList<string> permissions)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
            var role = new Role($"r-{Ids.New():N}"[..20], "SSO test role", string.Empty, isSystem: false, PrincipalKind.User, permissions);
            context.Roles.Add(role);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return role.Code;
        }

        public async Task<Guid> UnitAsync()
        {
            await using var scope = app.Services.CreateAsyncScope();
            var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
            var root = (await units.RootAsync(TestContext.Current.CancellationToken))!.Id;
            var created = await units.CreateAsync(root, "New employees", $"s-{Ids.New():N}"[..12], KernelOrganisationKinds.Unit, TestContext.Current.CancellationToken);
            return created.Value.Id;
        }

        public async Task SetAsync(string key, JsonElement value)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var set = await scope.ServiceProvider.GetRequiredService<SettingsWriter>().SetAsync(key, SettingPlace.Tenant, value, TestContext.Current.CancellationToken);
            Assert.True(set.IsSuccess, set.IsFailure ? set.Error.ToString() : null);
        }

        /// <summary>Enrols an authenticator app for the person from My profile, in a fresh session; answers its secret.</summary>
        public async Task<byte[]> EnrolAsync(Principal person)
        {
            var session = await SessionOfAsync(person);
            using var setup = await SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app", session);
            var secret = Totp.FromBase32((await JsonAsync(setup)).RootElement.GetProperty("secret").GetString()!);
            using var confirmed = await SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app/confirm", session, new { code = Code(secret), name = "Phone" });
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            return secret;
        }

        public async Task<SsoProvider?> ProviderNamedAsync(string name)
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().SsoProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Name == name, TestContext.Current.CancellationToken);
        }

        public async Task<Principal?> PrincipalAsync(string loginId)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var normalised = Principal.NormaliseLoginId(loginId);
            return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Principals.AsNoTracking().SingleOrDefaultAsync(p => p.LoginId == normalised, TestContext.Current.CancellationToken);
        }

        public async Task<int> CountPrincipalsAsync(string loginId)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var normalised = Principal.NormaliseLoginId(loginId);
            return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Principals.CountAsync(p => p.LoginId == normalised || p.Email == normalised, TestContext.Current.CancellationToken);
        }

        public async Task<IReadOnlyList<SsoLink>> LinksAsync(Guid principalId)
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().SsoLinks.AsNoTracking().Where(l => l.PrincipalId == principalId).ToListAsync(TestContext.Current.CancellationToken);
        }

        public async Task<IReadOnlyList<Held>> AssignmentsAsync(Guid principalId)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
            var rows = await context.RoleAssignments.AsNoTracking().Where(a => a.PrincipalId == principalId)
                .Join(context.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new { Assignment = a, r.Code })
                .ToListAsync(TestContext.Current.CancellationToken);
            return [.. rows.Select(row => new Held(row.Code, row.Assignment.ToScope(), row.Assignment.Source))];
        }

        public async Task<IReadOnlyList<SecurityEvent>> SecurityEventsAsync(Guid principalId)
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await PasswordPolicyTests.SecurityEventsAsync(scope.ServiceProvider, principalId);
        }

        /// <summary>The newest audit event of a type, about a target when one is named, with an outcome when one is named.</summary>
        public async Task<AuditEventRecord?> AuditAsync(string eventType, string? target, string? outcomeName = null)
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
                .Where(e => e.EventType == eventType && (target == null || e.Target == target) && (outcomeName == null || e.Outcome == outcomeName))
                .OrderByDescending(e => e.Id)
                .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        }

        public async Task<IReadOnlyList<string>> AuditReasonsAsync(string eventType)
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
                .Where(e => e.EventType == eventType && e.Reason != null)
                .Select(e => e.Reason!)
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        public async Task<string?> SecondFactorOfAsync(string sessionCookie)
        {
            var hash = SecureTokens.Hash(sessionCookie.Split('=', 2)[1]);
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Sessions.AsNoTracking().Where(s => s.TokenHash == hash).Select(s => s.SecondFactor).SingleAsync(TestContext.Current.CancellationToken);
        }

        public async Task<Guid?> ProviderOfAsync(string sessionCookie)
        {
            var hash = SecureTokens.Hash(sessionCookie.Split('=', 2)[1]);
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Sessions.AsNoTracking().Where(s => s.TokenHash == hash).Select(s => s.ProviderId).SingleAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>The client secret as the store holds it, read with the store's root token at the unit's path.</summary>
        public async Task<string?> StoredSecretAsync(Guid providerId)
        {
            using var store = new OpenBaoSecretStore(new SecretStoreOptions { Address = sso.Store.Address, Token = sso.Store.RootToken, Mount = sso.Store.Mount, Path = storePath });
            return (await store.ReadAsync($"{storePath}/sso/{providerId:N}", TestContext.Current.CancellationToken))?.GetValueOrDefault(StoreSsoClientSecrets.SecretKey);
        }

        /// <summary>Every text column of every table in the unit's database that holds <paramref name="text"/>: what a dump would show.</summary>
        public async Task<IReadOnlyList<string>> TextsHoldingAsync(string text)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var columns = new List<(string Schema, string Table, string Column)>();
            await using (var list = new NpgsqlCommand("select table_schema, table_name, column_name from information_schema.columns where table_schema like 'kernel_%' and data_type in ('text', 'character varying', 'jsonb')", connection))
            await using (var reader = await list.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            {
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                }
            }

            var found = new List<string>();
            foreach (var (schema, table, column) in columns)
            {
                await using var command = new NpgsqlCommand($"select count(*) from \"{schema}\".\"{table}\" where \"{column}\"::text like '%' || @text || '%'", connection);
                command.Parameters.AddWithValue("text", text);
                if ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))! > 0)
                {
                    found.Add($"{schema}.{table}.{column}");
                }
            }

            return found;
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }
}
