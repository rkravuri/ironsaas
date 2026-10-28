using System.Text.Json;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Settings.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Identity.Tests;

/// <summary>The password policy, its settings, history and lockout (S3 T2, TF-012, ADR-0046).</summary>
[Collection(IdentityCollection.Name)]
public class PasswordPolicyTests(IdentityFixture fixture)
{
    private const string Password = "correct horse battery staple";

    /// <summary>On the NCSC's list, and long enough that only the list refuses it; picked at run time (ADR-0054).</summary>
    private static readonly string CommonPassword = IdentityFixture.CommonPassword();

    [Fact]
    [Proves("TF-012 AC4", "ADR-0036", "ADR-0046")]
    public async Task ThePolicyStartsAtItsDefaultsAndEachSettingKeepsToItsLimits()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = await fixture.CreateTenantDatabaseAsync($"limits_{Ids.New():N}");
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), connectionString: tenant);
        await using var scope = provider.CreateAsyncScope();
        var rules = await scope.ServiceProvider.GetRequiredService<PasswordPolicy>().RulesAsync(ct);
        var lockout = await scope.ServiceProvider.GetRequiredService<LoginThrottles>().RulesAsync(ct);
        var writer = scope.ServiceProvider.GetRequiredService<SettingsWriter>();
        var cases = new (string Key, object[] Accepted, object[] Refused)[]
        {
            (IdentitySettings.PasswordMinimumLength.Key, [8, 64], [7, 65]),
            (IdentitySettings.PasswordCommonCheck.Key, [true], [false]),
            (IdentitySettings.PasswordHistoryDepth.Key, [0, 24], [-1, 25]),
            (IdentitySettings.PasswordMaximumAgeDays.Key, [0, 30, 365], [-1, 29, 366]),
            (IdentitySettings.LockoutThreshold.Key, [3, 10], [2, 11]),
            (IdentitySettings.LockoutWindowMinutes.Key, [5, 60], [4, 61]),
            (IdentitySettings.LockoutDurationMinutes.Key, [5, 120], [4, 121]),
        };

        Assert.Equal(new PasswordRules(12, 1024, true, 10, 0), rules);
        Assert.Equal(new LockoutRules(5, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)), lockout);
        foreach (var (key, accepted, refused) in cases)
        {
            foreach (var value in accepted)
            {
                Assert.True((await writer.SetAsync(key, SettingPlace.Tenant, JsonSerializer.SerializeToElement(value), ct)).IsSuccess, $"{key} = {value} should be accepted");
            }

            foreach (var value in refused)
            {
                Assert.True((await writer.SetAsync(key, SettingPlace.Tenant, JsonSerializer.SerializeToElement(value), ct)).IsFailure, $"{key} = {value} should be refused");
            }
        }
    }

    [Fact]
    [Proves("TF-012 AC4", "ADR-0046")]
    public async Task TheTenantsSettingsChangeTheRulesOnTheNextCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = await fixture.CreateTenantDatabaseAsync($"rules_{Ids.New():N}");
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), connectionString: tenant);
        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<SettingsWriter>();
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
        var login = scope.ServiceProvider.GetRequiredService<LoginService>();
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"rules-{Ids.New():N}@acme.test", Password);
        await Set(writer, IdentitySettings.PasswordMinimumLength.Key, 16);
        await Set(writer, IdentitySettings.PasswordHistoryDepth.Key, 0);
        await Set(writer, IdentitySettings.LockoutThreshold.Key, 3);

        var fifteen = await passwords.ChangeAsync(user.Id, null, Password, "fifteen letters", true, ct);
        var reused = await passwords.ChangeAsync(user.Id, null, Password, Password, true, ct);
        var failures = new List<string>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            failures.Add((await login.LoginAsync(user.LoginId, "not the password", ct)).Error.Code);
        }

        Assert.Equal(IdentityErrors.PasswordTooShort, fifteen.Error.Code);
        Assert.Contains("16", fifteen.Error.Message, StringComparison.Ordinal);
        Assert.True(reused.IsSuccess); // a depth of 0 allows any earlier password
        Assert.Equal([IdentityErrors.LoginInvalid, IdentityErrors.LoginInvalid, IdentityErrors.LoginLocked], failures);
    }

    [Fact]
    [Proves("TF-012 AC1", "ADR-0046")]
    public async Task ACommonPasswordIsRefusedWithItsReasonOnEveryPathThatSetsOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        var invited = await IdentityFixture.CreateInvitedUserAsync(scope.ServiceProvider, $"common-{Ids.New():N}@acme.test");
        var invitation = await scope.ServiceProvider.GetRequiredService<InvitationService>().InviteAsync(invited, ct);
        var active = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"common-{Ids.New():N}@acme.test", Password);
        var recovery = await OpenRecoveryAsync(scope.ServiceProvider, active);
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();

        var refused = new[]
        {
            await scope.ServiceProvider.GetRequiredService<InvitationService>().AcceptAsync(invitation, CommonPassword, null, ct) is { IsFailure: true } accept ? accept.Error : null,
            await passwords.ChangeAsync(active.Id, null, Password, CommonPassword.ToUpperInvariant(), true, ct) is { IsFailure: true } change ? change.Error : null,
            await passwords.RecoverAsync(recovery, CommonPassword, ct) is { IsFailure: true } recover ? recover.Error : null,
        };

        Assert.All(refused, error =>
        {
            Assert.NotNull(error);
            Assert.Equal(IdentityErrors.PasswordCommon, error.Code);
            Assert.Contains("most common passwords", error.Message, StringComparison.Ordinal);
            Assert.Equal(("password", PasswordPolicy.Rules.Common), (Assert.Single(error.Fields).Field, error.Fields[0].Code));
        });
    }

    [Fact]
    [Proves("TF-012 AC3", "ADR-0046")]
    public async Task APasswordAmongTheLastTenIsRefusedAndAnOlderOneIsAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"history-{Ids.New():N}@acme.test", Password);
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
        var current = Password;
        for (var change = 1; change <= 10; change++)
        {
            time.Advance(TimeSpan.FromDays(1));
            var next = $"history password number {change}";
            Assert.True((await passwords.ChangeAsync(user.Id, null, current, next, false, ct)).IsSuccess);
            current = next;
        }

        var theCurrentOne = await passwords.ChangeAsync(user.Id, null, current, current, false, ct);
        var theFifth = await passwords.ChangeAsync(user.Id, null, current, "history password number 5", false, ct);
        var theFirst = await passwords.ChangeAsync(user.Id, null, current, Password, false, ct); // eleven back: outside the ten
        var kept = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().PasswordHistory.CountAsync(e => e.PrincipalId == user.Id, ct);

        Assert.Equal(IdentityErrors.PasswordReused, theCurrentOne.Error.Code);
        Assert.Equal(IdentityErrors.PasswordReused, theFifth.Error.Code);
        Assert.Equal(PasswordPolicy.Rules.Reused, Assert.Single(theFifth.Error.Fields).Code);
        Assert.True(theFirst.IsSuccess);
        Assert.Equal(12, kept); // the first password, ten changes and the last, under the 24 kept
    }

    [Fact]
    [Proves("TF-012 AC2", "TF-011 AC2", "ADR-0046")]
    public async Task FiveFailuresLockAKnownLoginEvenAgainstTheCorrectPasswordUntilTheDurationPasses()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"lock-{Ids.New():N}@acme.test", Password);
        var login = scope.ServiceProvider.GetRequiredService<LoginService>();

        var answers = new List<string>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            answers.Add((await login.LoginAsync(user.LoginId, "not the password", ct)).Error.Code);
            time.Advance(TimeSpan.FromMinutes(2));
        }

        var sixth = await login.LoginAsync(user.LoginId, Password, ct);
        time.Advance(TimeSpan.FromMinutes(14));
        var later = await login.LoginAsync(user.LoginId, Password, ct);
        var events = await SecurityEventsAsync(scope.ServiceProvider, user.Id);
        var audit = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .SingleAsync(e => e.EventType == IdentityAuditEvents.LoginLocked && e.RecordId == user.Id, ct);

        Assert.Equal([.. Enumerable.Repeat(IdentityErrors.LoginInvalid, 4), IdentityErrors.LoginLocked], answers);
        Assert.Equal((IdentityErrors.LoginLocked, LoginService.LockedMessage), (sixth.Error.Code, sixth.Error.Message));
        Assert.True(later.IsSuccess); // fifteen minutes after the lock, the correct password works again
        Assert.Equal((SecurityEventKinds.LoginLocked, SecurityEventActors.System), (Assert.Single(events).Kind, events[0].Actor));
        Assert.Equal(user.LoginId, audit.Target);
    }

    [Fact]
    [Proves("TF-012 AC2", "TF-011 AC2", "ADR-0046")]
    public async Task AnUnknownLoginLocksTheSameWaySoTheAnswerRevealsNoAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        var known = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"known-{Ids.New():N}@acme.test", Password);
        var unknown = $"nobody-{Ids.New():N}@acme.test";
        var login = scope.ServiceProvider.GetRequiredService<LoginService>();

        Error? knownAnswer = null, unknownAnswer = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            knownAnswer = (await login.LoginAsync(known.LoginId, "not the password", ct)).Error;
            unknownAnswer = (await login.LoginAsync(unknown, "not the password", ct)).Error;
        }

        var lockAudit = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .SingleAsync(e => e.EventType == IdentityAuditEvents.LoginLocked && e.Target == unknown, ct);

        Assert.Equal((IdentityErrors.LoginLocked, LoginService.LockedMessage), (knownAnswer!.Code, knownAnswer.Message));
        Assert.Equal((knownAnswer.Code, knownAnswer.Message, knownAnswer.Kind), (unknownAnswer!.Code, unknownAnswer.Message, unknownAnswer.Kind));
        Assert.Equal(Guid.Empty, lockAudit.ActorId);
        Assert.Null(lockAudit.RecordId); // nobody's security event: there is nobody
    }

    [Fact]
    [Proves("TF-012 AC2", "ADR-0046")]
    public async Task FailuresOutsideTheWindowDoNotAddUpAndASuccessForgetsThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"window-{Ids.New():N}@acme.test", Password);
        var login = scope.ServiceProvider.GetRequiredService<LoginService>();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await login.LoginAsync(user.LoginId, "not the password", ct);
        }

        time.Advance(TimeSpan.FromMinutes(16));
        var afterTheWindow = await login.LoginAsync(user.LoginId, "not the password", ct);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await login.LoginAsync(user.LoginId, "not the password", ct);
        }

        var success = await login.LoginAsync(user.LoginId, Password, ct);
        var throttled = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().LoginThrottles.AnyAsync(t => t.LoginId == user.LoginId, ct);

        Assert.Equal(IdentityErrors.LoginInvalid, afterTheWindow.Error.Code);
        Assert.True(success.IsSuccess); // four failures in the new window, then the right password
        Assert.False(throttled);
    }

    [Fact]
    [Proves("TF-012 AC5", "ADR-0046")]
    public async Task APasswordPastTheMaximumAgeSignsInToARecoveryLinkInsteadOfASession()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var tenant = await fixture.CreateTenantDatabaseAsync($"age_{Ids.New():N}");
        await using var provider = fixture.BuildServices(time, connectionString: tenant);
        await using var scope = provider.CreateAsyncScope();
        await Set(scope.ServiceProvider.GetRequiredService<SettingsWriter>(), IdentitySettings.PasswordMaximumAgeDays.Key, 30);
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"age-{Ids.New():N}@acme.test", Password);
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
        Assert.True((await passwords.ChangeAsync(user.Id, null, Password, "a password with a date", false, ct)).IsSuccess);
        var login = scope.ServiceProvider.GetRequiredService<LoginService>();

        time.Advance(TimeSpan.FromDays(29));
        var young = await login.LoginAsync(user.LoginId, "a password with a date", ct);
        time.Advance(TimeSpan.FromDays(1));
        var old = await login.LoginAsync(user.LoginId, "a password with a date", ct);
        var renewed = await passwords.RecoverAsync(old.Value.RecoveryToken!, "a newer password with a date", ct);
        var again = await login.LoginAsync(user.LoginId, "a newer password with a date", ct);

        Assert.NotNull(young.Value.Session);
        Assert.True(old.Value.PasswordExpired);
        Assert.Null(old.Value.Session);
        Assert.True(renewed.IsSuccess);
        Assert.NotNull(again.Value.Session);
    }

    internal static async Task<string> OpenRecoveryAsync(IServiceProvider services, Principal principal)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transaction = await services.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        var token = await services.GetRequiredService<PasswordService>().OpenRecoveryAsync(principal, SecurityEventActors.Administrator, "Test Administrator", null, null, ct);
        await transaction.CommitAsync(ct);
        return token;
    }

    internal static async Task<IReadOnlyList<SecurityEvent>> SecurityEventsAsync(IServiceProvider services, Guid principalId) =>
        await services.GetRequiredService<IdentityDbContext>().SecurityEvents.AsNoTracking()
            .Where(e => e.PrincipalId == principalId)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

    private static async Task Set(SettingsWriter writer, string key, int value) =>
        Assert.True((await writer.SetAsync(key, SettingPlace.Tenant, JsonSerializer.SerializeToElement(value), TestContext.Current.CancellationToken)).IsSuccess);
}
