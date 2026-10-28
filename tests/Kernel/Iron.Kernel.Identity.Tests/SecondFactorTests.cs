using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Encryption;
using Iron.Kernel.Handover;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
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
using Npgsql;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// Second factors and step-up over HTTP (S3 T3, ADR-0047), each on a tenant database of its own, so the tenant's MFA
/// policy is the default (roles that hold a high-risk or critical permission) unless the test sets it.
/// </summary>
[Collection(IdentityCollection.Name)]
public class SecondFactorTests(IdentityFixture fixture)
{
    private const string Password = "correct horse battery staple";
    private const string SessionCookie = "iron.session";
    private const string SignInCookie = AuthenticationChallenges.CookieName;

    [Fact]
    [Proves("TF-013 AC1", "ADR-0047")]
    public async Task APersonWhoMustHaveAFactorEnrolsBeforeAnyPageAndAPasswordAloneNeverSignsThemIn()
    {
        await using var host = await StartAsync();
        var person = await host.PersonAsync([IdentityPermissions.UsersManage.Name]);

        using var login = await host.LoginAsync(person.LoginId);
        var step = (await JsonAsync(login)).RootElement.GetProperty("step");
        var signIn = Cookie(login, SignInCookie)!;
        using var page = await host.SendAsync(HttpMethod.Get, "/api/v1/me", signIn);
        using var wrongDoor = await host.SendAsync(HttpMethod.Post, "/auth/second-factor", signIn, new { code = "123456" });
        using var setup = await host.SendAsync(HttpMethod.Post, "/auth/enrolment/authenticator-app", signIn);
        var secret = Totp.FromBase32((await JsonAsync(setup)).RootElement.GetProperty("secret").GetString()!);
        using var wrong = await host.SendAsync(HttpMethod.Post, "/auth/enrolment/authenticator-app/confirm", signIn, new { code = "000000" });
        using var enrolled = await host.SendAsync(HttpMethod.Post, "/auth/enrolment/authenticator-app/confirm", signIn, new { code = host.Code(secret), name = "My phone" });
        var enrolledBody = await JsonAsync(enrolled);
        var session = Cookie(enrolled, SessionCookie)!;
        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);

        host.Time.Advance(TimeSpan.FromSeconds(31));
        using var again = await host.LoginAsync(person.LoginId);
        var secondStep = (await JsonAsync(again)).RootElement.GetProperty("step");
        using var second = await host.SendAsync(HttpMethod.Post, "/auth/second-factor", Cookie(again, SignInCookie), new { code = host.Code(secret) });
        var events = await host.SecurityEventsAsync(person.Id);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("enrolment", step.GetProperty("kind").GetString());
        Assert.Equal(["authenticator_app", "passkey"], step.GetProperty("methods").EnumerateArray().Select(m => m.GetString()));
        Assert.Null(Cookie(login, SessionCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
        Assert.Equal(IdentityErrors.SignInExpired, await ProblemAsync(wrongDoor, HttpStatusCode.Unauthorized));
        Assert.Equal(IdentityErrors.FactorInvalid, await ProblemAsync(wrong, HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        Assert.Equal(10, enrolledBody.RootElement.GetProperty("recovery_codes").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("second_factor", secondStep.GetProperty("kind").GetString());
        Assert.Equal(["authenticator_app", "recovery_code"], secondStep.GetProperty("methods").EnumerateArray().Select(m => m.GetString()));
        Assert.Null(Cookie(again, SessionCookie));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotNull(Cookie(second, SessionCookie));
        Assert.Contains(events, e => e.Kind == SecurityEventKinds.FactorEnrolled && e.Actor == SecurityEventActors.Self);
    }

    [Fact]
    [Proves("TF-013 AC1", "ADR-0047")]
    public async Task ThePolicyRequiresAFactorOfNobodyOfTheHoldersOfRolesOrOfEveryoneAndATenantMayGiveGrace()
    {
        await using var host = await StartAsync();
        var plain = await host.PersonAsync([]);
        var risky = await host.PersonAsync([IdentityPermissions.UsersManage.Name]);
        var named = await host.PersonAsync([IdentityPermissions.UsersRead.Name], roleCode: $"mfa-{Ids.New():N}"[..20]);

        async Task<string> StepOf(Principal person)
        {
            host.Time.Advance(TimeSpan.FromSeconds(1));
            using var login = await host.LoginAsync(person.LoginId);
            var body = (await JsonAsync(login)).RootElement;
            return body.TryGetProperty("step", out var step) && step.ValueKind == JsonValueKind.Object ? step.GetProperty("kind").GetString()! : "session";
        }

        var byDefault = (await StepOf(plain), await StepOf(risky), await StepOf(named.Person));
        await host.SetAsync(IdentitySettings.MfaRoles.Key, JsonSerializer.SerializeToElement(new[] { named.RoleCode! }));
        var byNamedRole = (await StepOf(plain), await StepOf(risky), await StepOf(named.Person));
        await host.SetAsync(IdentitySettings.MfaRequiredFor.Key, JsonSerializer.SerializeToElement(MfaRequirement.Everyone));
        var forEveryone = (await StepOf(plain), await StepOf(risky), await StepOf(named.Person));
        await host.SetAsync(IdentitySettings.MfaRequiredFor.Key, JsonSerializer.SerializeToElement(MfaRequirement.Nobody));
        var forNobody = (await StepOf(plain), await StepOf(risky), await StepOf(named.Person));

        await host.SetAsync(IdentitySettings.MfaRequiredFor.Key, JsonSerializer.SerializeToElement(MfaRequirement.Everyone));
        await host.SetAsync(IdentitySettings.MfaGraceDays.Key, JsonSerializer.SerializeToElement(7));
        var graced = await host.PersonAsync([]);
        using var first = await host.LoginAsync(graced.LoginId);
        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", Cookie(first, SessionCookie));
        var enrolBy = (await JsonAsync(me)).RootElement.GetProperty("mfa_enrol_by").GetDateTimeOffset();
        var since = host.Time.GetUtcNow();
        host.Time.Advance(TimeSpan.FromDays(8));
        var afterGrace = await StepOf(graced);
        using var token = await host.TokenAsync(IdentityFixture.SmokeClientId, IdentityFixture.SmokeClientSecret);

        Assert.Equal(("session", "enrolment", "session"), byDefault);
        Assert.Equal(("session", "session", "enrolment"), byNamedRole);
        Assert.Equal(("enrolment", "enrolment", "enrolment"), forEveryone);
        Assert.Equal(("session", "session", "session"), forNobody);
        Assert.NotNull(Cookie(first, SessionCookie)); // within the grace, the password still signs them in
        Assert.Equal(since.AddDays(7), enrolBy);
        Assert.Equal("enrolment", afterGrace);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode); // a service account keeps its client credentials
    }

    [Fact]
    [Proves("TF-013 AC1", "ADR-0047")]
    public async Task APolicyChangeTakesEffectAtTheNextRequestAndTheSessionGoesOnAfterEnrollingInIt()
    {
        await using var host = await StartAsync(MfaRequirement.Nobody);
        var person = await host.PersonAsync([IdentityPermissions.UsersManage.Name]);
        var graced = await host.PersonAsync([IdentityPermissions.UsersManage.Name]);
        using var login = await host.LoginAsync(person.LoginId);
        var session = Cookie(login, SessionCookie)!;
        using var gracedLogin = await host.LoginAsync(graced.LoginId);
        var gracedSession = Cookie(gracedLogin, SessionCookie)!;
        using var before = await host.SendAsync(HttpMethod.Get, "/api/v1/me/security-events", session);

        // The tenant now requires a factor of the holders of high-risk roles: the next request says so, and the session stays.
        await host.SetAsync(IdentitySettings.MfaRequiredFor.Key, JsonSerializer.SerializeToElement(MfaRequirement.Roles));
        using var refused = await host.SendAsync(HttpMethod.Get, "/api/v1/me/security-events", session);
        using var users = await host.SendAsync(HttpMethod.Get, "/api/v1/users", session);
        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
        var meBody = (await JsonAsync(me)).RootElement;
        using var setup = await host.SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app", session);
        var secret = Totp.FromBase32((await JsonAsync(setup)).RootElement.GetProperty("secret").GetString()!);
        using var enrolled = await host.SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app/confirm", session, new { code = host.Code(secret), name = "Phone" });
        var codes = (await JsonAsync(enrolled)).RootElement.GetProperty("recovery_codes").GetArrayLength();
        using var after = await host.SendAsync(HttpMethod.Get, "/api/v1/me/security-events", session);
        using var meAfter = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);

        // With a grace of three days, the same change lets the other person work on, and says by when; then it does not.
        await host.SetAsync(IdentitySettings.MfaGraceDays.Key, JsonSerializer.SerializeToElement(3));
        var graceStarts = host.Time.GetUtcNow();
        using var withinGrace = await host.SendAsync(HttpMethod.Get, "/api/v1/me/security-events", gracedSession);
        using var gracedMe = await host.SendAsync(HttpMethod.Get, "/api/v1/me", gracedSession);
        var enrolBy = (await JsonAsync(gracedMe)).RootElement.GetProperty("mfa_enrol_by").GetDateTimeOffset();
        host.Time.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        using var fresh = await host.LoginAsync(graced.LoginId);

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(IdentityErrors.MfaEnrolmentRequired, await ProblemAsync(refused, HttpStatusCode.Forbidden));
        Assert.Equal(IdentityErrors.MfaEnrolmentRequired, await ProblemAsync(users, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode); // the session stays: nobody is signed out
        Assert.True(meBody.GetProperty("mfa_enrolment_required").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Equal((HttpStatusCode.OK, 10), (enrolled.StatusCode, codes));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode); // the same session goes on after enrolling
        Assert.False((await JsonAsync(meAfter)).RootElement.GetProperty("mfa_enrolment_required").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, withinGrace.StatusCode);
        Assert.Equal(graceStarts.AddDays(3), enrolBy); // from the first request that found the factor required
        Assert.Equal("enrolment", (await JsonAsync(fresh)).RootElement.GetProperty("step").GetProperty("kind").GetString());
    }

    [Fact]
    [Proves("TF-013 AC1", "ADR-0047")]
    public async Task AnEnrolmentAtSignInHasFifteenMinutesAndTheSecondStepFive()
    {
        await using var host = await StartAsync();
        var newcomer = await host.PersonAsync([IdentityPermissions.UsersManage.Name]);
        var enrolledPerson = await host.PersonAsync([]);
        var (_, secret, _) = await host.EnrolAsync(enrolledPerson);

        using var enrolment = await host.LoginAsync(newcomer.LoginId);
        var cookie = enrolment.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith($"{SignInCookie}=", StringComparison.Ordinal));
        host.Time.Advance(TimeSpan.FromMinutes(14));
        using var stillOpen = await host.SendAsync(HttpMethod.Post, "/auth/enrolment/authenticator-app", Cookie(enrolment, SignInCookie));
        host.Time.Advance(TimeSpan.FromMinutes(2));
        using var tooLate = await host.SendAsync(HttpMethod.Post, "/auth/enrolment/authenticator-app", Cookie(enrolment, SignInCookie));

        using var secondStep = await host.LoginAsync(enrolledPerson.LoginId);
        host.Time.Advance(TimeSpan.FromMinutes(6));
        using var expired = await host.SendAsync(HttpMethod.Post, "/auth/second-factor", Cookie(secondStep, SignInCookie), new { code = host.Code(secret) });

        Assert.Contains("max-age=900", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, stillOpen.StatusCode);
        Assert.Equal(IdentityErrors.SignInExpired, await ProblemAsync(tooLate, HttpStatusCode.Unauthorized));
        Assert.Equal(IdentityErrors.SignInExpired, await ProblemAsync(expired, HttpStatusCode.Unauthorized));
    }

    [Fact]
    [Proves("TF-013 AC2", "ADR-0047")]
    public async Task EveryThirdWrongCodeCoolsSixtySecondsDoublingAndTheTenthLocksTheLogin()
    {
        await using var host = await StartAsync();
        var person = await host.PersonAsync([]);
        var (_, secret, _) = await host.EnrolAsync(person);
        var answers = new List<string>();
        var waits = new List<int>();

        async Task WrongAsync(string signIn)
        {
            using var answer = await host.SendAsync(HttpMethod.Post, "/auth/second-factor", signIn, new { code = "000000" });
            using var problem = await JsonAsync(answer);
            answers.Add(problem.RootElement.GetProperty("error_code").GetString()!);
            if (problem.RootElement.TryGetProperty("retry_after_seconds", out var wait))
            {
                waits.Add(wait.GetInt32());
            }
        }

        string? coolingRight = null;
        foreach (var cooling in new[] { 60, 120, 240 })
        {
            using var login = await host.LoginAsync(person.LoginId);
            var signIn = Cookie(login, SignInCookie)!;
            await WrongAsync(signIn);
            await WrongAsync(signIn);
            await WrongAsync(signIn);
            if (coolingRight is null)
            {
                host.Time.Advance(TimeSpan.FromSeconds(30));
                using var right = await host.SendAsync(HttpMethod.Post, "/auth/second-factor", signIn, new { code = host.Code(secret) });
                coolingRight = await ProblemAsync(right, HttpStatusCode.Unauthorized);
                host.Time.Advance(TimeSpan.FromSeconds(cooling - 30));
            }
            else
            {
                host.Time.Advance(TimeSpan.FromSeconds(cooling));
            }
        }

        using var last = await host.LoginAsync(person.LoginId);
        await WrongAsync(Cookie(last, SignInCookie)!);
        using var locked = await host.LoginAsync(person.LoginId);
        var events = await host.SecurityEventsAsync(person.Id);

        string Invalid = IdentityErrors.SecondFactorInvalid, Cooling = IdentityErrors.SecondFactorCooling;
        Assert.Equal([Invalid, Invalid, Cooling, Invalid, Invalid, Cooling, Invalid, Invalid, Cooling, IdentityErrors.LoginLocked], answers);
        Assert.Equal([60, 120, 240], waits);
        Assert.Equal(Cooling, coolingRight); // during the cooling period even the right code is not checked
        Assert.Equal(IdentityErrors.LoginLocked, await ProblemAsync(locked, HttpStatusCode.Unauthorized)); // the password is refused too
        Assert.Contains(events, e => e.Kind == SecurityEventKinds.LoginLocked && e.Actor == SecurityEventActors.System);
    }

    [Fact]
    [Proves("TF-013 AC3", "ADR-0047")]
    public async Task ARecoveryCodeSignsInOnceAsksForANewFactorAndANewSetReplacesTheOld()
    {
        await using var host = await StartAsync();
        var person = await host.PersonAsync([]);
        var (session, _, codes) = await host.EnrolAsync(person);
        using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/me/factors", session);
        var listedBody = await JsonAsync(listed);

        using var login = await host.LoginAsync(person.LoginId);
        using var recovered = await host.SendAsync(HttpMethod.Post, "/auth/second-factor", Cookie(login, SignInCookie), new { recovery_code = codes[0].ToLowerInvariant() });
        var recoveredBody = await JsonAsync(recovered);
        var recoveredSession = Cookie(recovered, SessionCookie)!;
        using var left = await host.SendAsync(HttpMethod.Get, "/api/v1/me/factors", recoveredSession);
        using var loginAgain = await host.LoginAsync(person.LoginId);
        using var reused = await host.SendAsync(HttpMethod.Post, "/auth/second-factor", Cookie(loginAgain, SignInCookie), new { recovery_code = codes[0] });

        using var regenerated = await host.SendAsync(HttpMethod.Post, "/api/v1/me/recovery-codes", recoveredSession);
        var fresh = (await JsonAsync(regenerated)).RootElement.GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList();
        using var oldLogin = await host.LoginAsync(person.LoginId);
        using var old = await host.SendAsync(HttpMethod.Post, "/auth/second-factor", Cookie(oldLogin, SignInCookie), new { recovery_code = codes[1] });
        using var newLogin = await host.LoginAsync(person.LoginId);
        using var renewed = await host.SendAsync(HttpMethod.Post, "/auth/second-factor", Cookie(newLogin, SignInCookie), new { recovery_code = fresh[0] });
        var events = await host.SecurityEventsAsync(person.Id);

        Assert.Equal(10, codes.Count);
        Assert.All(codes, code => Assert.Matches("^[A-Z2-7]{4}-[A-Z2-7]{4}-[A-Z2-7]{4}-[A-Z2-7]{4}$", code));
        Assert.Equal(10, listedBody.RootElement.GetProperty("recovery_codes_left").GetInt32());
        Assert.DoesNotContain(codes[0], listedBody.RootElement.GetRawText(), StringComparison.Ordinal); // shown once, at enrolment
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.True(recoveredBody.RootElement.GetProperty("enrol_new_factor").GetBoolean());
        Assert.Equal(9, (await JsonAsync(left)).RootElement.GetProperty("recovery_codes_left").GetInt32());
        Assert.Equal(IdentityErrors.SecondFactorInvalid, await ProblemAsync(reused, HttpStatusCode.Unauthorized));
        Assert.Equal(10, fresh.Count);
        Assert.Equal(IdentityErrors.SecondFactorInvalid, await ProblemAsync(old, HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.Equal(2, events.Count(e => e.Kind == SecurityEventKinds.RecoveryCodeUsed));
        Assert.Single(events, e => e.Kind == SecurityEventKinds.RecoveryCodesRegenerated);
    }

    [Fact]
    [Proves("ADR-0047")]
    public async Task APasskeyIsEnrolledWithItsNameProvesTheSecondStepAndSignsInAloneWhereTheTenantAllowsIt()
    {
        await using var host = await StartAsync();
        var person = await host.PersonAsync([IdentityPermissions.UsersManage.Name]);
        using var key = new SoftwareAuthenticator();
        using var spare = new SoftwareAuthenticator();

        using var login = await host.LoginAsync(person.LoginId);
        var signIn = Cookie(login, SignInCookie)!;
        using var creation = await host.SendAsync(HttpMethod.Post, "/auth/enrolment/passkey/options", signIn);
        var creationOptions = (await JsonAsync(creation)).RootElement.GetProperty("options");
        using var enrolled = await host.SendAsync(HttpMethod.Post, "/auth/enrolment/passkey", signIn, new { name = "Laptop", credential = key.Create(creationOptions) });
        var session = Cookie(enrolled, SessionCookie)!;

        using var again = await host.LoginAsync(person.LoginId);
        var methods = (await JsonAsync(again)).RootElement.GetProperty("step").GetProperty("methods").EnumerateArray().Select(m => m.GetString()).ToList();
        var secondStep = Cookie(again, SignInCookie)!;
        using var assertion = await host.SendAsync(HttpMethod.Post, "/auth/second-factor/passkey-options", secondStep);
        var assertionOptions = (await JsonAsync(assertion)).RootElement.GetProperty("options");
        var answer = key.Get(assertionOptions);
        using var proved = await host.SendAsync(HttpMethod.Post, "/auth/second-factor", secondStep, new { passkey = answer });

        using var replayLogin = await host.LoginAsync(person.LoginId);
        var replayStep = Cookie(replayLogin, SignInCookie)!;
        using var replayOptions = await host.SendAsync(HttpMethod.Post, "/auth/second-factor/passkey-options", replayStep);
        using var replayed = await host.SendAsync(HttpMethod.Post, "/auth/second-factor", replayStep, new { passkey = answer });

        using var offOptions = await host.SendAsync(HttpMethod.Get, "/auth/sign-in-options");
        using var off = await host.SendAsync(HttpMethod.Post, "/auth/passkey/options");
        await host.SetAsync(IdentitySettings.MfaPasskeySignIn.Key, JsonSerializer.SerializeToElement(true));
        using var onOptions = await host.SendAsync(HttpMethod.Get, "/auth/sign-in-options");
        using var discoverable = await host.SendAsync(HttpMethod.Post, "/auth/passkey/options");
        var discoverableOptions = (await JsonAsync(discoverable)).RootElement.GetProperty("options");
        using var alone = await host.SendAsync(HttpMethod.Post, "/auth/passkey", Cookie(discoverable, SignInCookie), new { credential = key.Get(discoverableOptions) });
        using var strangerOptions = await host.SendAsync(HttpMethod.Post, "/auth/passkey/options");
        using var stranger = await host.SendAsync(HttpMethod.Post, "/auth/passkey", Cookie(strangerOptions, SignInCookie), new { credential = spare.Get((await JsonAsync(strangerOptions)).RootElement.GetProperty("options")) });

        using var addOptions = await host.SendAsync(HttpMethod.Post, "/api/v1/me/factors/passkey/options", session);
        using var added = await host.SendAsync(HttpMethod.Post, "/api/v1/me/factors/passkey", session, new { name = "Spare key", credential = spare.Create((await JsonAsync(addOptions)).RootElement.GetProperty("options")) });
        var addedId = (await JsonAsync(added)).RootElement.GetProperty("factor").GetProperty("id").GetGuid();
        using var renamed = await host.SendAsync(HttpMethod.Patch, $"/api/v1/me/factors/{addedId}", session, new { name = "Key in the drawer" });
        using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/me/factors", session);
        var names = (await JsonAsync(listed)).RootElement.GetProperty("factors").EnumerateArray().Select(f => (f.GetProperty("kind").GetString(), f.GetProperty("name").GetString())).ToList();
        using var removed = await host.SendAsync(HttpMethod.Delete, $"/api/v1/me/factors/{addedId}", session);
        using var afterRemoval = await host.SendAsync(HttpMethod.Get, "/api/v1/me/factors", session);

        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        Assert.Equal("required", creationOptions.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
        Assert.Equal(["passkey", "recovery_code"], methods);
        Assert.Equal(HttpStatusCode.OK, proved.StatusCode);
        Assert.NotNull(Cookie(proved, SessionCookie));
        Assert.Equal(IdentityErrors.SecondFactorInvalid, await ProblemAsync(replayed, HttpStatusCode.Unauthorized)); // a replayed answer
        Assert.False((await JsonAsync(offOptions)).RootElement.GetProperty("passkey_sign_in").GetBoolean());
        Assert.Equal(IdentityErrors.PasskeySignInOff, await ProblemAsync(off, HttpStatusCode.Forbidden));
        Assert.True((await JsonAsync(onOptions)).RootElement.GetProperty("passkey_sign_in").GetBoolean());
        Assert.True(!discoverableOptions.TryGetProperty("allowCredentials", out var allowed) || allowed.GetArrayLength() == 0); // any passkey of this tenant
        Assert.Equal(HttpStatusCode.OK, alone.StatusCode);
        Assert.NotNull(Cookie(alone, SessionCookie));
        Assert.Equal(IdentityErrors.SecondFactorInvalid, await ProblemAsync(stranger, HttpStatusCode.Unauthorized)); // a passkey nobody here has
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal([("passkey", "Laptop"), ("passkey", "Key in the drawer")], names);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(1, (await JsonAsync(afterRemoval)).RootElement.GetProperty("factors").GetArrayLength());
    }

    [Fact]
    [Proves("TF-248 AC1", "TF-248 AC3", "ADR-0046", "ADR-0047")]
    public async Task AnAdministratorResetsFactorsWithAReasonARecentSignInAndEveryPermissionThePersonHolds()
    {
        await using var host = await StartAsync(MfaRequirement.Nobody);
        var unit = await host.UnitAsync();
        var person = await host.PersonAsync([IdentityPermissions.UsersRead.Name, "settings.values.manage"], unit);
        var unitAdministrator = await host.PersonAsync([IdentityPermissions.UsersRead.Name, IdentityPermissions.UsersManage.Name], unit, scopeUnit: unit);
        var administrator = await host.AdministratorAsync();
        using var personLogin = await host.LoginAsync(person.LoginId);
        var personSession = Cookie(personLogin, SessionCookie)!;
        await host.AddAppAsync(personSession);

        using var administratorLogin = await host.LoginAsync(administrator.LoginId);
        var administratorSession = Cookie(administratorLogin, SessionCookie)!;
        using var unitLogin = await host.LoginAsync(unitAdministrator.LoginId);
        using var record = await host.SendAsync(HttpMethod.Get, $"/api/v1/users/{person.Id}", administratorSession);
        var recordBody = (await JsonAsync(record)).RootElement;
        using var unitRecord = await host.SendAsync(HttpMethod.Get, $"/api/v1/users/{person.Id}", Cookie(unitLogin, SessionCookie));
        using var escalation = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/factors/reset", Cookie(unitLogin, SessionCookie), new { reason = "Lost phone" });
        using var noReason = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/factors/reset", administratorSession, new { reason = " " });
        host.Time.Advance(TimeSpan.FromMinutes(6));
        using var stale = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/factors/reset", administratorSession, new { reason = "Lost phone" });
        var staleFactors = await host.FactorCountAsync(person.Id);
        using var fresh = await host.LoginAsync(administrator.LoginId);
        using var reset = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/factors/reset", Cookie(fresh, SessionCookie), new { reason = "Lost phone on the train" });
        using var personAfter = await host.SendAsync(HttpMethod.Get, "/api/v1/me", personSession);
        var events = await host.SecurityEventsAsync(person.Id);

        Assert.Equal((1, true), (recordBody.GetProperty("factors").GetInt32(), recordBody.GetProperty("takeover_allowed").GetBoolean()));
        Assert.False((await JsonAsync(unitRecord)).RootElement.GetProperty("takeover_allowed").GetBoolean());
        Assert.Equal(IdentityErrors.FactorResetEscalation, await ProblemAsync(escalation, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(RecentAuthentication.ErrorCode, await ProblemAsync(stale, HttpStatusCode.Forbidden));
        Assert.Equal(1, staleFactors); // refused, and nothing changed
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(0, (await JsonAsync(reset)).RootElement.GetProperty("factors").GetInt32());
        Assert.Equal(0, await host.FactorCountAsync(person.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, personAfter.StatusCode); // every session of the person ended
        var resetEvent = Assert.Single(events, e => e.Kind == SecurityEventKinds.FactorsReset);
        Assert.Equal((SecurityEventActors.Administrator, administrator.DisplayName, "Lost phone on the train"), (resetEvent.Actor, resetEvent.ActorName, resetEvent.Reason));
    }

    [Fact]
    [Proves("TF-248 AC1", "TF-248 AC2", "TF-248 AC3", "TF-248 AC4", "ADR-0047")]
    public async Task HighRiskActionsGoOnWhenTheSignInIsRecentAndAfterAConfirmationWhenItIsNot()
    {
        await using var host = await StartAsync(MfaRequirement.Nobody);
        var person = await host.PersonAsync([]);
        var administrator = await host.AdministratorAsync();
        var riskyRole = await host.RoleAsync([IdentityPermissions.UsersManage.Name]);
        using var login = await host.LoginAsync(administrator.LoginId);
        var session = Cookie(login, SessionCookie)!;
        var secret = await host.AddAppAsync(session);
        using var whileRecent = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/recovery-link", session);

        host.Time.Advance(TimeSpan.FromMinutes(6));
        using var link = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/recovery-link", session);
        using var deactivate = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/deactivate", session);
        using var grant = await host.SendAsync(HttpMethod.Post, "/api/v1/role-assignments", session, new { principal_id = person.Id, role_id = riskyRole, scope_kind = "tenant" });
        using var policy = await host.SendAsync(HttpMethod.Put, $"/api/v1/settings/{IdentitySettings.MfaRequiredFor.Key}", session, new { scope = "tenant", value = MfaRequirement.Everyone });
        using var harmless = await host.SendAsync(HttpMethod.Put, $"/api/v1/settings/{IdentitySettings.SessionIdleMinutes.Key}", session, new { scope = "tenant", value = 45 });
        var issuedBefore = (await host.SecurityEventsAsync(person.Id)).Count(e => e.Kind == SecurityEventKinds.RecoveryLinkIssued);

        using var asks = await host.SendAsync(HttpMethod.Post, "/api/v1/me/reauthentication", session);
        var asksBody = (await JsonAsync(asks)).RootElement;
        using var wrongPassword = await host.SendAsync(HttpMethod.Post, "/api/v1/me/reauthenticate", session, new { password = "not the password", code = host.Code(secret) });
        using var wrongCode = await host.SendAsync(HttpMethod.Post, "/api/v1/me/reauthenticate", session, new { password = Password, code = "000000" });
        using var stillOpen = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
        using var confirmed = await host.SendAsync(HttpMethod.Post, "/api/v1/me/reauthenticate", session, new { password = Password, code = host.Code(secret) });
        using var linkAfter = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/recovery-link", session);
        using var grantAfter = await host.SendAsync(HttpMethod.Post, "/api/v1/role-assignments", session, new { principal_id = person.Id, role_id = riskyRole, scope_kind = "tenant" });
        using var policyAfter = await host.SendAsync(HttpMethod.Put, $"/api/v1/settings/{IdentitySettings.MfaRequiredFor.Key}", session, new { scope = "tenant", value = MfaRequirement.Everyone });
        await using var scope = host.App.Services.CreateAsyncScope();
        var audited = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().CountAsync(e => e.EventType == IdentityAuditEvents.Reauthenticated && e.ActorId == administrator.Id, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, whileRecent.StatusCode);
        Assert.All(new[] { link, deactivate, grant, policy }, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        Assert.Equal(RecentAuthentication.ErrorCode, await ProblemAsync(link, HttpStatusCode.Forbidden));
        Assert.Equal(RecentAuthentication.ErrorCode, await ProblemAsync(deactivate, HttpStatusCode.Forbidden));
        Assert.Equal(RecentAuthentication.ErrorCode, await ProblemAsync(grant, HttpStatusCode.Forbidden));
        Assert.Equal(RecentAuthentication.ErrorCode, await ProblemAsync(policy, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.OK, harmless.StatusCode); // a setting that is not high-risk asks nothing
        Assert.Equal(1, issuedBefore); // only the recent one was issued
        Assert.True(asksBody.GetProperty("second_factor").GetBoolean());
        Assert.Equal(IdentityErrors.ReauthenticationInvalid, await ProblemAsync(wrongPassword, HttpStatusCode.Forbidden));
        Assert.Equal(IdentityErrors.SecondFactorInvalid, await ProblemAsync(wrongCode, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.OK, stillOpen.StatusCode);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, linkAfter.StatusCode);
        Assert.Equal(HttpStatusCode.Created, grantAfter.StatusCode);
        Assert.Equal(HttpStatusCode.OK, policyAfter.StatusCode);
        Assert.Equal(1, audited);
    }

    [Fact]
    [Proves("TF-074 AC1", "ADR-0047")]
    public async Task AnAuthenticatorAppsSecretIsSealedInTheDatabaseAndNoLogLineHoldsASecretOrACode()
    {
        var logs = new ConcurrentQueue<string>();
        await using var host = await StartAsync(logs: logs);
        var person = await host.PersonAsync([IdentityPermissions.UsersManage.Name]);

        using var login = await host.LoginAsync(person.LoginId);
        var signIn = Cookie(login, SignInCookie)!;
        using var setup = await host.SendAsync(HttpMethod.Post, "/auth/enrolment/authenticator-app", signIn);
        var text = (await JsonAsync(setup)).RootElement.GetProperty("secret").GetString()!;
        var secret = Totp.FromBase32(text);
        var pending = Assert.Single(await host.ReadTextsAsync("select held_secret from kernel_identity.authentication_challenges where principal_id = @id and held_secret is not null", person.Id));
        var code = host.Code(secret);
        using var enrolled = await host.SendAsync(HttpMethod.Post, "/auth/enrolment/authenticator-app/confirm", signIn, new { code });
        var codes = (await JsonAsync(enrolled)).RootElement.GetProperty("recovery_codes").EnumerateArray().Select(c => c.GetString()!).ToList();
        var stored = Assert.Single(await host.ReadTextsAsync("select secret from kernel_identity.factors where principal_id = @id", person.Id));
        var hashes = await host.ReadTextsAsync("select code_hash from kernel_identity.recovery_codes where principal_id = @id", person.Id);
        var opened = await host.App.Services.GetRequiredService<FactorSecrets>().OpenAsync(new EncryptedValue(stored), person.Id, TestContext.Current.CancellationToken);

        Assert.StartsWith("local:v1:", stored, StringComparison.Ordinal);
        Assert.StartsWith("local:v1:", pending, StringComparison.Ordinal);
        Assert.DoesNotContain(text, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(secret), stored, StringComparison.Ordinal);
        Assert.Equal(secret, opened);
        Assert.Equal(10, hashes.Count);
        Assert.All(codes, c => Assert.DoesNotContain(hashes, h => h.Contains(RecoveryCodes.Normalise(c), StringComparison.Ordinal)));
        Assert.Contains(logs, line => line.Contains("enrolled a factor", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains(text, StringComparison.Ordinal) || line.Contains(code, StringComparison.Ordinal) || codes.Any(c => line.Contains(c, StringComparison.Ordinal)));
    }

    [Fact]
    [Proves("ADR-0047")]
    public async Task TheOperatorsRecoveryResetsTheAdministratorsFactorsOnlyWhenAsked()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = await fixture.CreateTenantDatabaseAsync($"opfactors_{Ids.New():N}");
        var handover = new Handover();
        const string Operator = "Olga Operator";
        const string Reason = "The administrator lost their phone and their password";
        var loginId = $"admin-{Ids.New():N}@acme.test";
        await using (var setup = fixture.BuildServices(new FakeTimeProvider(), connectionString: tenant))
        {
            await using var scope = setup.CreateAsyncScope();
            var administrator = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, loginId, Password);
            Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(administrator.Id, SystemRoles.TenantAdministrator, ct)).IsSuccess);
            var secrets = scope.ServiceProvider.GetRequiredService<FactorSecrets>();
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            context.Factors.Add(Factor.AuthenticatorApp(administrator.Id, "Phone", await secrets.SealAsync(Totp.NewSecret(), administrator.Id, ct), 1, new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero)));
            await context.SaveChangesAsync(ct);
        }

        ServiceProvider Recovering(bool reset) => fixture.BuildServices(
            new FakeTimeProvider(),
            connectionString: tenant,
            settings: new Dictionary<string, string?>
            {
                ["Recovery:LoginId"] = loginId,
                ["Recovery:Operator"] = Operator,
                ["Recovery:Reason"] = Reason,
                ["Recovery:ResetFactors"] = reset ? "true" : "false",
            },
            configure: services => services.AddSingleton<IOperatorHandover>(handover));

        await using var withoutReset = Recovering(reset: false);
        var keptExit = await DatabaseCommands.RunAsync(withoutReset, [DatabaseCommands.RecoverAdministrator], ct);
        int kept;
        await using (var scope = withoutReset.CreateAsyncScope())
        {
            kept = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Factors.CountAsync(ct);
        }

        await using var withReset = Recovering(reset: true);
        var resetExit = await DatabaseCommands.RunAsync(withReset, [DatabaseCommands.RecoverAdministrator], ct);
        await using var after = withReset.CreateAsyncScope();
        var identity = after.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var principal = await identity.Principals.AsNoTracking().SingleAsync(p => p.LoginId == loginId, ct);
        var events = await PasswordPolicyTests.SecurityEventsAsync(after.ServiceProvider, principal.Id);
        var audit = await after.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .SingleAsync(e => e.EventType == SecurityEventKinds.AuditEventOf(SecurityEventKinds.FactorsReset) && e.RecordId == principal.Id, ct);

        Assert.Equal((0, 1), (keptExit, kept));
        Assert.Equal(0, resetExit);
        Assert.Equal(0, await identity.Factors.CountAsync(f => f.PrincipalId == principal.Id, ct));
        Assert.Equal(2, handover.Count); // a recovery link both times
        var reset = Assert.Single(events, e => e.Kind == SecurityEventKinds.FactorsReset);
        Assert.Equal((SecurityEventActors.Operator, Operator, Reason), (reset.Actor, reset.ActorName, reset.Reason));
        Assert.Equal(Reason, audit.Reason);
        Assert.Equal(Operator, JsonDocument.Parse(audit.Details!).RootElement.GetProperty("operator").GetString());
    }

    private async Task<TestHost> StartAsync(string? mfa = null, ConcurrentQueue<string>? logs = null)
    {
        var connectionString = await fixture.CreateTenantDatabaseAsync($"mfa_{Ids.New():N}", mfa: true);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-22T09:00:05Z", System.Globalization.CultureInfo.InvariantCulture));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.AddProvider(new QueueLogs(logs));
        }

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
        var host = new TestHost(app, app.GetTestClient(), time, connectionString);
        if (mfa is not null)
        {
            await host.SetAsync(IdentitySettings.MfaRequiredFor.Key, JsonSerializer.SerializeToElement(mfa));
        }

        return host;
    }

    private static string? Cookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith($"{name}=", StringComparison.Ordinal) && v.Length > name.Length + 1)
            : null;

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static async Task<string?> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = await JsonAsync(response);
        return problem.RootElement.GetProperty("error_code").GetString();
    }

    /// <summary>One in-memory unit on a tenant database of its own, with its clock, and the steps the tests repeat.</summary>
    private sealed class TestHost(WebApplication app, HttpClient client, FakeTimeProvider time, string connectionString) : IAsyncDisposable
    {
        public WebApplication App => app;

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

        public async Task<HttpResponseMessage> TokenAsync(string clientId, string secret)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/connect/token", UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        /// <summary>A person with a password and, when given permissions, a role holding them, at the tenant or at a unit.</summary>
        public async Task<Principal> PersonAsync(IReadOnlyList<string> permissions, Guid? homeUnit = null, Guid? scopeUnit = null) =>
            (await PersonAsync(permissions, null, homeUnit, scopeUnit)).Person;

        public async Task<(Principal Person, string? RoleCode)> PersonAsync(IReadOnlyList<string> permissions, string? roleCode, Guid? homeUnit = null, Guid? scopeUnit = null)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var context = services.GetRequiredService<IdentityDbContext>();
            var person = new Principal($"mfa-{Ids.New():N}@acme.test", PrincipalType.User, $"Person {Ids.New():N}"[..15], null);
            person.AcceptInvitation(services.GetRequiredService<IPasswordHasher>().Hash(Password), null);
            person.SetHomeUnit(homeUnit ?? (await services.GetRequiredService<OrganisationUnitService>().RootAsync(TestContext.Current.CancellationToken))!.Id);
            context.Principals.Add(person);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            if (permissions.Count == 0)
            {
                return (person, null);
            }

            var role = await RoleAsync(permissions, roleCode);
            var code = await services.GetRequiredService<AuthorizationDbContext>().Roles.AsNoTracking().Where(r => r.Id == role).Select(r => r.Code).SingleAsync(TestContext.Current.CancellationToken);
            var assigned = await services.GetRequiredService<IRoleAssignments>().AssignAsync(person.Id, code, scopeUnit is { } unit ? RoleScope.Unit(unit, includeChildren: true) : RoleScope.Tenant, TestContext.Current.CancellationToken);
            Assert.True(assigned.IsSuccess, assigned.IsFailure ? assigned.Error.Message : null);
            return (person, code);
        }

        public async Task<Principal> AdministratorAsync()
        {
            var person = await PersonAsync([]);
            await using var scope = app.Services.CreateAsyncScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(person.Id, SystemRoles.TenantAdministrator, TestContext.Current.CancellationToken)).IsSuccess);
            return person;
        }

        public async Task<Guid> RoleAsync(IReadOnlyList<string> permissions, string? code = null)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
            var role = new Role(code ?? $"r-{Ids.New():N}"[..20], "MFA test role", string.Empty, isSystem: false, PrincipalKind.User, permissions);
            context.Roles.Add(role);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return role.Id;
        }

        public async Task<Guid> UnitAsync()
        {
            await using var scope = app.Services.CreateAsyncScope();
            var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
            var root = (await units.RootAsync(TestContext.Current.CancellationToken))!.Id;
            var created = await units.CreateAsync(root, "Second factors", $"f-{Ids.New():N}"[..12], KernelOrganisationKinds.Unit, TestContext.Current.CancellationToken);
            return created.Value.Id;
        }

        /// <summary>A tenant setting, written as the system: the tests set the policy, the step-up test changes it over HTTP.</summary>
        public async Task SetAsync(string key, JsonElement value)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var set = await scope.ServiceProvider.GetRequiredService<SettingsWriter>().SetAsync(key, SettingPlace.Tenant, value, TestContext.Current.CancellationToken);
            Assert.True(set.IsSuccess, set.IsFailure ? set.Error.ToString() : null);
        }

        /// <summary>Signs a person in with a password and enrols an authenticator app at sign-in under the policy everyone.</summary>
        public async Task<(string Session, byte[] Secret, IReadOnlyList<string> Codes)> EnrolAsync(Principal person)
        {
            await SetAsync(IdentitySettings.MfaRequiredFor.Key, JsonSerializer.SerializeToElement(MfaRequirement.Everyone));
            using var login = await LoginAsync(person.LoginId);
            var signIn = Cookie(login, SignInCookie)!;
            using var setup = await SendAsync(HttpMethod.Post, "/auth/enrolment/authenticator-app", signIn);
            var secret = Totp.FromBase32((await JsonAsync(setup)).RootElement.GetProperty("secret").GetString()!);
            using var enrolled = await SendAsync(HttpMethod.Post, "/auth/enrolment/authenticator-app/confirm", signIn, new { code = Code(secret) });
            var codes = (await JsonAsync(enrolled)).RootElement.GetProperty("recovery_codes").EnumerateArray().Select(c => c.GetString()!).ToList();
            time.Advance(TimeSpan.FromSeconds(30));
            return (Cookie(enrolled, SessionCookie)!, secret, codes);
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

        public async Task<int> FactorCountAsync(Guid principalId)
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Factors.CountAsync(f => f.PrincipalId == principalId, TestContext.Current.CancellationToken);
        }

        public async Task<IReadOnlyList<SecurityEvent>> SecurityEventsAsync(Guid principalId)
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await PasswordPolicyTests.SecurityEventsAsync(scope.ServiceProvider, principalId);
        }

        /// <summary>A column as the database holds it, read past the application: what a dump would show.</summary>
        public async Task<byte[]> ReadBytesAsync(string sql, Guid principalId)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", principalId);
            return (byte[])(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        public async Task<IReadOnlyList<string>> ReadTextsAsync(string sql, Guid principalId)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", principalId);
            var texts = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                texts.Add(reader.GetString(0));
            }

            return texts;
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    /// <summary>The fleet's handover place as the command sees it: how many links it was given.</summary>
    private sealed class Handover : IOperatorHandover
    {
        private int _count;

        public int Count => _count;

        public bool IsAvailable => true;

        public Task DepositAsync(OperatorHandoverItem item, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }

    /// <summary>Every log line, formatted, with its values, as a provider would write them.</summary>
    private sealed class QueueLogs(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(lines);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lines.Enqueue(formatter(state, exception));
                if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    foreach (var value in values)
                    {
                        lines.Enqueue(value.Value?.ToString() ?? string.Empty);
                    }
                }
            }
        }
    }
}
