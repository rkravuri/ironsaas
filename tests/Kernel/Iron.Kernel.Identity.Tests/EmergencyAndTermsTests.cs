using System.Net;
using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Security.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Identity.Tests;

/// <summary>A tenant's emergency accounts, and terms acceptance (S3 T5, TF-028, ADR-0049).</summary>
[Collection(IdentityCollection.Name)]
public class EmergencyAndTermsTests(IdentityFixture fixture)
{
    [Fact]
    [Proves("ADR-0049")]
    public async Task AnEmergencyAccountNeedsASecondFactorIsOneOfTwoAtMostAndIsNeverSingleSignOnOnly()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (_, admin) = await host.AdministratorAsync();
        var without = await host.PersonAsync();
        var first = await host.PersonAsync();
        var second = await host.PersonAsync();
        var third = await host.PersonAsync();
        foreach (var person in new[] { first, second, third })
        {
            await EnrolAsync(host, person);
        }

        using var noFactor = await host.SendAsync(HttpMethod.Put, $"/api/v1/users/{without.Id}/emergency", admin, new { emergency = true });
        using var one = await host.SendAsync(HttpMethod.Put, $"/api/v1/users/{first.Id}/emergency", admin, new { emergency = true });
        using var two = await host.SendAsync(HttpMethod.Put, $"/api/v1/users/{second.Id}/emergency", admin, new { emergency = true });
        using var three = await host.SendAsync(HttpMethod.Put, $"/api/v1/users/{third.Id}/emergency", admin, new { emergency = true });
        using var ssoOnly = await host.SendAsync(HttpMethod.Put, $"/api/v1/users/{first.Id}/sso-only", admin, new { sso_only = true });
        using var record = await host.SendAsync(HttpMethod.Get, $"/api/v1/users/{first.Id}", admin);

        Assert.Equal(IdentityErrors.EmergencyNeedsFactor, await UnitHost.ProblemAsync(noFactor, HttpStatusCode.Conflict));
        Assert.True((await UnitHost.JsonAsync(one)).GetProperty("emergency").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, two.StatusCode);
        Assert.Equal(IdentityErrors.EmergencyLimit, await UnitHost.ProblemAsync(three, HttpStatusCode.Conflict));
        Assert.Equal(IdentityErrors.UserEmergency, await UnitHost.ProblemAsync(ssoOnly, HttpStatusCode.Conflict));
        Assert.True((await UnitHost.JsonAsync(record)).GetProperty("emergency").GetBoolean());
        Assert.Contains(await host.SecurityEventsAsync(first.Id), e => e.Kind == SecurityEventKinds.EmergencySet);
    }

    [Fact]
    [Proves("ADR-0049", "TF-014 AC2")]
    public async Task AnEmergencyAccountSignsInWithItsPasswordWhenEveryoneElseMustUseTheCompanyLogin()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var held = await host.PersonAsync();
        var emergency = await host.PersonAsync();
        await EnrolAsync(host, emergency);
        await host.WithScopeAsync(async services =>
        {
            var context = services.GetRequiredService<IdentityDbContext>();
            foreach (var person in await context.Principals.Where(p => p.Id == held.Id || p.Id == emergency.Id).ToListAsync(TestContext.Current.CancellationToken))
            {
                person.MarkSsoOnly(true);
                person.MarkEmergency(person.Id == emergency.Id);
            }

            return await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        using var refused = await host.LoginAsync(held.LoginId);
        using var allowed = await host.LoginAsync(emergency.LoginId);

        Assert.Equal(IdentityErrors.UseCompanyLogin, await UnitHost.ProblemAsync(refused, HttpStatusCode.Unauthorized));
        Assert.Equal("second_factor", (await UnitHost.JsonAsync(allowed)).GetProperty("step").GetProperty("kind").GetString());
    }

    [Fact]
    [Proves("ADR-0049", "TF-013 AC1")]
    public async Task AnEmergencyAccountWithoutAFactorEnrolsBeforeItsSessionWhateverThePolicySays()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var person = await host.PersonAsync();
        await host.WithScopeAsync(async services =>
        {
            var context = services.GetRequiredService<IdentityDbContext>();
            (await context.Principals.SingleAsync(p => p.Id == person.Id, TestContext.Current.CancellationToken)).MarkEmergency(true);
            return await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        using var login = await host.LoginAsync(person.LoginId);

        Assert.Equal("enrolment", (await UnitHost.JsonAsync(login)).GetProperty("step").GetProperty("kind").GetString());
        Assert.Null(UnitHost.Cookie(login, UnitHost.SessionCookie));
    }

    [Fact]
    [Proves("ADR-0049")]
    public async Task EverySignInWithAnEmergencyAccountRaisesAnAlertTheAdministratorsSee()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (_, admin) = await host.AdministratorAsync();
        var reader = await host.SessionOfAsync(await host.PersonWithRoleAsync(SystemRoles.ReadOnly));
        var person = await host.PersonAsync(displayName: "Erin Emergency");
        var secret = await EnrolAsync(host, person);
        using var marked = await host.SendAsync(HttpMethod.Put, $"/api/v1/users/{person.Id}/emergency", admin, new { emergency = true });

        host.Time.Advance(TimeSpan.FromSeconds(30));
        using var login = await host.LoginAsync(person.LoginId);
        var signIn = UnitHost.Cookie(login, UnitHost.SignInCookie)!;
        using var proved = await host.SendAsync(HttpMethod.Post, "/auth/second-factor", signIn, new { code = Totp.Code(secret, Totp.StepAt(host.Time.GetUtcNow())) });
        using var alerts = await host.SendAsync(HttpMethod.Get, "/api/v1/security-alerts", admin);
        using var forbidden = await host.SendAsync(HttpMethod.Get, "/api/v1/security-alerts", reader);
        var audited = Assert.Single(await host.AuditAsync(SecurityEventKinds.AuditEventOf(SecurityEventKinds.EmergencySignIn), person.LoginId));
        var rows = (await UnitHost.JsonAsync(alerts)).GetProperty("emergency_sign_ins").EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        Assert.NotNull(UnitHost.Cookie(proved, UnitHost.SessionCookie));
        Assert.Equal(AuditSeverity.High, audited.Severity);
        Assert.Equal(("Erin Emergency", person.LoginId), (rows.Single().GetProperty("display_name").GetString(), rows.Single().GetProperty("login_id").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains(await host.SecurityEventsAsync(person.Id), e => e.Kind == SecurityEventKinds.EmergencySignIn);
    }

    [Fact]
    [Proves("TF-028 AC1", "ADR-0049")]
    public async Task ANewVersionMustBeAcceptedAtTheNextRequestBeforeAnythingElse()
    {
        var platform = new FakePlatformDocuments();
        await using var host = await StartAsync(platform);
        var (_, admin) = await host.AdministratorAsync();
        var person = await host.PersonWithRoleAsync(SystemRoles.ReadOnly);
        var session = await host.SessionOfAsync(person);
        using var before = await host.SendAsync(HttpMethod.Get, "/api/v1/roles", session);

        using var published = await host.SendAsync(HttpMethod.Post, "/api/v1/documents", admin, new { key = "code-of-conduct", title = "Code of conduct", body = "Be kind." });
        platform.Publish(new PublishedDocument(PlatformDocumentKeys.TermsOfService, 3, "Terms of service", "The platform's terms.", host.Time.GetUtcNow()));
        using var held = await host.SendAsync(HttpMethod.Get, "/api/v1/roles", session);
        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
        using var pending = await host.SendAsync(HttpMethod.Get, "/api/v1/me/terms", session);
        var documents = (await UnitHost.JsonAsync(pending)).GetProperty("pending").EnumerateArray().ToList();
        using var partly = await host.SendAsync(HttpMethod.Post, "/api/v1/me/terms/accept", session, new { documents = new[] { new { source = "platform", key = PlatformDocumentKeys.TermsOfService, version = 3 } } });
        using var accepted = await host.SendAsync(HttpMethod.Post, "/api/v1/me/terms/accept", session, new
        {
            documents = new[] { new { source = "platform", key = PlatformDocumentKeys.TermsOfService, version = 3 }, new { source = "tenant", key = "code-of-conduct", version = 1 } },
        });
        using var after = await host.SendAsync(HttpMethod.Get, "/api/v1/roles", session);

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        Assert.Equal(IdentityErrors.TermsAcceptanceRequired, await UnitHost.ProblemAsync(held, HttpStatusCode.Forbidden));
        Assert.True((await UnitHost.JsonAsync(me)).GetProperty("terms_acceptance_required").GetBoolean());
        Assert.Equal([("platform", PlatformDocumentKeys.TermsOfService, 3), ("tenant", "code-of-conduct", 1)], documents.Select(d => (d.GetProperty("source").GetString(), d.GetProperty("key").GetString(), d.GetProperty("version").GetInt32())));
        Assert.Equal("Be kind.", documents[1].GetProperty("body").GetString());
        Assert.Equal(IdentityErrors.TermsVersionChanged, await UnitHost.ProblemAsync(partly, HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    [Proves("TF-028 AC2", "ADR-0049")]
    public async Task EachAcceptanceKeepsVersionTimeAndPersonForTheAdministratorsWithAnExport()
    {
        await using var host = await StartAsync(new FakePlatformDocuments());
        var (_, admin) = await host.AdministratorAsync();
        var person = await host.PersonAsync(displayName: "Ann, \"the\" Acceptor");
        var session = await host.SessionOfAsync(person);
        using var first = await host.SendAsync(HttpMethod.Post, "/api/v1/documents", admin, new { key = "privacy", title = "Privacy at Acme", body = "Version one." });
        host.Time.Advance(TimeSpan.FromMinutes(1));
        using var accepted = await host.SendAsync(HttpMethod.Post, "/api/v1/me/terms/accept", session, new { documents = new[] { new { source = "tenant", key = "privacy", version = 1 } } });
        var acceptedAt = host.Time.GetUtcNow();
        await AcceptAllAsync(host, admin); // the administrator is a person of the tenant too
        using var second = await host.SendAsync(HttpMethod.Post, "/api/v1/documents", admin, new { key = "privacy", title = "Privacy at Acme", body = "Version two." });
        using var heldAgain = await host.SendAsync(HttpMethod.Get, "/api/v1/directory?search=an", session);
        await AcceptAllAsync(host, admin);

        using var list = await host.SendAsync(HttpMethod.Get, $"/api/v1/documents/acceptances?filter=principal_id:eq:{person.Id}", admin);
        var row = Assert.Single((await UnitHost.JsonAsync(list)).GetProperty("data").EnumerateArray());
        using var documents = await host.SendAsync(HttpMethod.Get, "/api/v1/documents", admin);
        var privacy = (await UnitHost.JsonAsync(documents)).GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("key").GetString() == "privacy");
        using var export = await host.SendAsync(HttpMethod.Get, $"/api/v1/documents/acceptances/export?filter=principal_id:eq:{person.Id}", admin);
        var csv = await export.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var stored = await host.WithScopeAsync(services => services.GetRequiredService<IdentityDbContext>().DocumentAcceptances.AsNoTracking().SingleAsync(a => a.PrincipalId == person.Id, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(("tenant", "privacy", 1, person.LoginId), (row.GetProperty("source").GetString(), row.GetProperty("document_key").GetString(), row.GetProperty("version").GetInt32(), row.GetProperty("login_id").GetString()));
        Assert.True((stored.AcceptedAt - acceptedAt).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.Equal((2, 1), (privacy.GetProperty("version").GetInt32(), privacy.GetProperty("accepted").GetInt32())); // the administrator has accepted version two; the person not yet
        Assert.Equal(IdentityErrors.TermsAcceptanceRequired, await UnitHost.ProblemAsync(heldAgain, HttpStatusCode.Forbidden)); // version two waits for them
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("login_id,display_name,source,document,version,accepted_at\r\n", csv, StringComparison.Ordinal);
        Assert.Contains($"{person.LoginId},\"Ann, \"\"the\"\" Acceptor\",tenant,privacy,1,", csv, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    [Fact]
    [Proves("TF-028 AC1", "ADR-0049")]
    public async Task ARetiredDocumentIsAskedOfNobodyAndANewVersionAsksItAgain()
    {
        await using var host = await StartAsync(new FakePlatformDocuments());
        var (_, admin) = await host.AdministratorAsync();
        var person = await host.PersonAsync();
        var session = await host.SessionOfAsync(person);
        using var published = await host.SendAsync(HttpMethod.Post, "/api/v1/documents", admin, new { key = "e2e-terms", title = "A test document", body = "Text." });
        using var held = await host.SendAsync(HttpMethod.Get, "/api/v1/directory?search=an", session);
        using var retired = await host.SendAsync(HttpMethod.Post, "/api/v1/documents/e2e-terms/retire", admin);
        using var free = await host.SendAsync(HttpMethod.Get, "/api/v1/directory?search=an", session);
        using var twice = await host.SendAsync(HttpMethod.Post, "/api/v1/documents/e2e-terms/retire", admin);
        using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/documents", admin, new { key = "e2e-terms", title = "A test document", body = "Text, again." });
        await AcceptAllAsync(host, admin);
        using var heldAgain = await host.SendAsync(HttpMethod.Get, "/api/v1/directory?search=an", session);

        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        Assert.Equal(IdentityErrors.TermsAcceptanceRequired, await UnitHost.ProblemAsync(held, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.NoContent, retired.StatusCode); // the administrator was held by it too, and could still retire it
        Assert.Equal(HttpStatusCode.OK, free.StatusCode);
        Assert.Equal(IdentityErrors.DocumentNotFound, await UnitHost.ProblemAsync(twice, HttpStatusCode.NotFound));
        Assert.Equal(2, (await UnitHost.JsonAsync(again, HttpStatusCode.Created)).GetProperty("version").GetInt32());
        Assert.Equal(IdentityErrors.TermsAcceptanceRequired, await UnitHost.ProblemAsync(heldAgain, HttpStatusCode.Forbidden));
    }

    [Fact]
    [Proves("TF-028 AC3")]
    public async Task AServiceAccountNeverAcceptsAnything()
    {
        var platform = new FakePlatformDocuments();
        platform.Publish(new PublishedDocument(PlatformDocumentKeys.PrivacyNotice, 1, "Privacy notice", "Notice.", DateTimeOffset.UtcNow));
        await using var host = await StartAsync(platform);
        var client = await host.ServiceAccountAsync("a client secret of some length");
        var token = await host.TokenAsync(client, "a client secret of some length");

        using var roles = await host.SendAsync(HttpMethod.Get, "/api/v1/roles", bearer: token);

        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
    }

    [Fact]
    [Proves("TF-028 AC4", "ADR-0049")]
    public async Task DecliningEndsTheSessionAndThePersonIsAskedAgainAtTheirNextSignIn()
    {
        var platform = new FakePlatformDocuments();
        platform.Publish(new PublishedDocument(PlatformDocumentKeys.TermsOfService, 1, "Terms of service", "Terms.", DateTimeOffset.UtcNow));
        await using var host = await StartAsync(platform);
        var person = await host.PersonAsync();
        var session = await host.SessionOfAsync(person);

        using var declined = await host.SendAsync(HttpMethod.Post, "/api/v1/me/terms/decline", session);
        using var after = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
        var again = await host.SessionOfAsync(person);
        using var stillHeld = await host.SendAsync(HttpMethod.Get, "/api/v1/directory?search=an", again);
        var audited = Assert.Single(await host.AuditAsync(IdentityAuditEvents.TermsDeclined, person.LoginId));

        Assert.Equal(HttpStatusCode.NoContent, declined.StatusCode);
        Assert.Contains(declined.Headers.GetValues("Set-Cookie"), c => c.StartsWith(UnitHost.SessionCookie + "=;", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal(IdentityErrors.TermsAcceptanceRequired, await UnitHost.ProblemAsync(stillHeld, HttpStatusCode.Forbidden));
        Assert.Equal(SessionEndReasons.TermsDeclined, (await host.SessionsAsync(person.Id))[0].EndReason);
        Assert.Equal(AuditOutcome.Denied, audited.Outcome);
    }

    [Fact]
    [Proves("TF-028 AC1", "TF-025 AC5", "ADR-0049")]
    public async Task TermsComeBeforeEnrolmentAndAnImpersonatorNeitherWaitsForNorAcceptsThem()
    {
        var platform = new FakePlatformDocuments();
        await using var host = await StartAsync(platform);
        var (_, admin) = await host.AdministratorAsync();
        var person = await host.PersonAsync();
        var session = await host.SessionOfAsync(person);
        using var started = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/impersonation", admin, new { reason = "Help with the new terms" });
        var both = $"{admin}; {UnitHost.Cookie(started, "iron.impersonation")}";

        // Both steps pending for the person: a second factor now required of everyone, and a new version of the terms.
        await host.SetAsync(IdentitySettings.MfaRequiredFor.Key, MfaRequirement.Everyone);
        platform.Publish(new PublishedDocument(PlatformDocumentKeys.TermsOfService, 1, "Terms of service", "Terms.", host.Time.GetUtcNow()));
        using var held = await host.SendAsync(HttpMethod.Get, "/api/v1/directory?search=an", session);
        using var impersonating = await host.SendAsync(HttpMethod.Get, "/api/v1/directory?search=an", both);
        using var acceptAsImpersonator = await host.SendAsync(HttpMethod.Post, "/api/v1/me/terms/accept", both, new { documents = new[] { new { source = "platform", key = PlatformDocumentKeys.TermsOfService, version = 1 } } });
        using var enrolDuringTerms = await host.SendAsync(HttpMethod.Get, "/api/v1/me/factors", session);
        using var accepted = await host.SendAsync(HttpMethod.Post, "/api/v1/me/terms/accept", session, new { documents = new[] { new { source = "platform", key = PlatformDocumentKeys.TermsOfService, version = 1 } } });
        using var then = await host.SendAsync(HttpMethod.Get, "/api/v1/directory?search=an", session);

        Assert.Equal(IdentityErrors.TermsAcceptanceRequired, await UnitHost.ProblemAsync(held, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.OK, impersonating.StatusCode);
        Assert.Equal(ImpersonationRules.ForbiddenCode, await UnitHost.ProblemAsync(acceptAsImpersonator, HttpStatusCode.Forbidden));
        Assert.Equal(IdentityErrors.TermsAcceptanceRequired, await UnitHost.ProblemAsync(enrolDuringTerms, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(IdentityErrors.MfaEnrolmentRequired, await UnitHost.ProblemAsync(then, HttpStatusCode.Forbidden));
    }

    [Fact]
    [Proves("TF-028 AC1", "ADR-0049")]
    public async Task AUnitReadsThePlatformsDocumentsFromTheStoreAtMostOnceAMinute()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new MemoryStore();
        var documents = new StorePlatformDocuments(store, time, NullLogger<StorePlatformDocuments>.Instance);
        var first = new PublishedDocument(PlatformDocumentKeys.TermsOfService, 1, "Terms", "One.", time.GetUtcNow());
        store.Values[PlatformDocumentsStore.Path] = new Dictionary<string, string> { [PlatformDocumentsStore.DocumentKey] = PlatformDocumentsStore.ToJson([first]) };

        var read = await documents.CurrentAsync(TestContext.Current.CancellationToken);
        store.Values[PlatformDocumentsStore.Path] = new Dictionary<string, string> { [PlatformDocumentsStore.DocumentKey] = PlatformDocumentsStore.ToJson([first with { Version = 2 }]) };
        var cached = await documents.CurrentAsync(TestContext.Current.CancellationToken);
        time.Advance(StorePlatformDocuments.CacheFor);
        var fresh = await documents.CurrentAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, read.Single().Version);
        Assert.Equal(1, cached.Single().Version);
        Assert.Equal(2, fresh.Single().Version);
        Assert.Equal(2, store.Reads);
    }

    private Task<UnitHost> StartAsync(FakePlatformDocuments platform) =>
        UnitHost.StartAsync(fixture, services: services => services.Replace(ServiceDescriptor.Singleton<IPlatformDocuments>(platform)));

    /// <summary>Accepts every version the session's person must accept now.</summary>
    private static async Task AcceptAllAsync(UnitHost host, string session)
    {
        using var pending = await host.SendAsync(HttpMethod.Get, "/api/v1/me/terms", session);
        var documents = (await UnitHost.JsonAsync(pending)).GetProperty("pending").EnumerateArray()
            .Select(d => new { source = d.GetProperty("source").GetString(), key = d.GetProperty("key").GetString(), version = d.GetProperty("version").GetInt32() })
            .ToList();
        using var accepted = await host.SendAsync(HttpMethod.Post, "/api/v1/me/terms/accept", session, new { documents });
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    /// <summary>Enrols an authenticator app for the person from My profile; answers its secret.</summary>
    private static async Task<byte[]> EnrolAsync(UnitHost host, Principal person)
    {
        var session = await host.SessionOfAsync(person);
        using var setup = await host.SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app", session);
        var secret = Totp.FromBase32((await UnitHost.JsonAsync(setup)).GetProperty("secret").GetString()!);
        using var confirmed = await host.SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app/confirm", session, new { code = Totp.Code(secret, Totp.StepAt(host.Time.GetUtcNow())), name = "Phone" });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        host.Time.Advance(TimeSpan.FromSeconds(30));
        return secret;
    }

    /// <summary>The platform's documents as the control plane published them, changed by the test.</summary>
    private sealed class FakePlatformDocuments : IPlatformDocuments
    {
        private readonly List<PublishedDocument> _documents = [];

        public void Publish(PublishedDocument document)
        {
            _documents.RemoveAll(d => d.Key == document.Key);
            _documents.Add(document);
        }

        public Task<IReadOnlyList<PublishedDocument>> CurrentAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PublishedDocument>>([.. _documents]);
    }

    /// <summary>A store in memory that counts its reads.</summary>
    private sealed class MemoryStore : ISecretStore
    {
        public Dictionary<string, IReadOnlyDictionary<string, string>> Values { get; } = new(StringComparer.Ordinal);

        public int Reads { get; private set; }

        public Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Values.GetValueOrDefault(path));
        }

        public Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
        {
            Values[path] = values;
            return Task.CompletedTask;
        }

        public Task<bool> IsReachableAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
