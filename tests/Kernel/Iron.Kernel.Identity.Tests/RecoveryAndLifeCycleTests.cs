using System.Text.Json;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Handover;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Identity.Tests;

/// <summary>Recovery links, the change of a password, and a person's life cycle (S3 T2, TF-010, TF-017, ADR-0046).</summary>
[Collection(IdentityCollection.Name)]
public class RecoveryAndLifeCycleTests(IdentityFixture fixture)
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a brand new passphrase";

    [Fact]
    [Proves("TF-017 AC1", "TF-017 AC2", "ADR-0046")]
    public async Task ARecoveryLinkSetsThePasswordOnceEndsEverySessionAndIsRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"recover-{Ids.New():N}@acme.test", Password);
        var login = scope.ServiceProvider.GetRequiredService<LoginService>();
        Assert.True((await login.LoginAsync(user.LoginId, Password, ct)).IsSuccess);
        Assert.True((await login.LoginAsync(user.LoginId, Password, ct)).IsSuccess);
        var token = await PasswordPolicyTests.OpenRecoveryAsync(scope.ServiceProvider, user);
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();

        var first = await passwords.RecoverAsync(token, NewPassword, ct);
        var second = await passwords.RecoverAsync(token, "yet another passphrase", ct);
        var live = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Sessions.CountAsync(s => s.PrincipalId == user.Id && s.EndedAt == null, ct);
        var oldPassword = await login.LoginAsync(user.LoginId, Password, ct);
        var newPassword = await login.LoginAsync(user.LoginId, NewPassword, ct);
        var events = await PasswordPolicyTests.SecurityEventsAsync(scope.ServiceProvider, user.Id);
        var audited = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.RecordId == user.Id).Select(e => e.EventType).ToListAsync(ct);

        Assert.True(first.IsSuccess);
        Assert.Equal((ErrorKind.NotFound, IdentityErrors.RecoveryInvalid), (second.Error.Kind, second.Error.Code));
        Assert.Equal(0, live);
        Assert.True(oldPassword.IsFailure);
        Assert.True(newPassword.IsSuccess);
        Assert.Equal([SecurityEventKinds.RecoveryLinkIssued, SecurityEventKinds.RecoveryLinkUsed, SecurityEventKinds.PasswordSetByRecovery], events.Select(e => e.Kind));
        Assert.Equal(("Test Administrator", SecurityEventActors.Administrator), (events[0].ActorName, events[0].Actor));
        Assert.Equal(SecurityEventActors.Self, events[2].Actor);
        Assert.Contains(IdentityAuditEvents.RecoveryLinkIssued, audited);
        Assert.Contains(IdentityAuditEvents.RecoveryLinkUsed, audited);
        Assert.Contains(IdentityAuditEvents.PasswordSetByRecovery, audited);
    }

    [Fact]
    [Proves("TF-017 AC1", "ADR-0046")]
    public async Task ANewerLinkReplacesTheOlderOneAndALinkStopsWorkingAfterFifteenMinutes()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"replace-{Ids.New():N}@acme.test", Password);
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();

        var older = await PasswordPolicyTests.OpenRecoveryAsync(scope.ServiceProvider, user);
        var newer = await PasswordPolicyTests.OpenRecoveryAsync(scope.ServiceProvider, user);
        var replaced = await passwords.RecoverAsync(older, NewPassword, ct);
        time.Advance(TimeSpan.FromMinutes(15));
        var expired = await passwords.RecoverAsync(newer, NewPassword, ct);
        var withinTime = await PasswordPolicyTests.OpenRecoveryAsync(scope.ServiceProvider, user);
        time.Advance(TimeSpan.FromMinutes(14));
        var used = await passwords.RecoverAsync(withinTime, NewPassword, ct);

        Assert.Equal(IdentityErrors.RecoveryInvalid, replaced.Error.Code);
        Assert.Equal(IdentityErrors.RecoveryInvalid, expired.Error.Code);
        Assert.Contains("15 minutes", expired.Error.Message, StringComparison.Ordinal);
        Assert.True(used.IsSuccess);
    }

    [Fact]
    [Proves("TF-017 AC1", "ADR-0046")]
    public async Task ARefusedPasswordLeavesTheLinkOpenAndUsingTheLinkLiftsALock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"lifted-{Ids.New():N}@acme.test", Password);
        var login = scope.ServiceProvider.GetRequiredService<LoginService>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await login.LoginAsync(user.LoginId, "not the password", ct);
        }

        var token = await PasswordPolicyTests.OpenRecoveryAsync(scope.ServiceProvider, user);
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();

        var tooShort = await passwords.RecoverAsync(token, "short", ct);
        var set = await passwords.RecoverAsync(token, NewPassword, ct);
        var signedIn = await login.LoginAsync(user.LoginId, NewPassword, ct);

        Assert.Equal(IdentityErrors.PasswordTooShort, tooShort.Error.Code);
        Assert.True(set.IsSuccess);
        Assert.True(signedIn.IsSuccess);
    }

    [Fact]
    [Proves("TF-017 AC3", "TF-017 AC4", "ADR-0046")]
    public async Task AChangeNeedsTheCurrentPasswordAndEndsTheOtherSessionsWhenAsked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"change-{Ids.New():N}@acme.test", Password);
        var login = scope.ServiceProvider.GetRequiredService<LoginService>();
        var here = (await login.LoginAsync(user.LoginId, Password, ct)).Value.Session!;
        var elsewhere = (await login.LoginAsync(user.LoginId, Password, ct)).Value.Session!;
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var wrongCurrent = await passwords.ChangeAsync(user.Id, here.Id, "not the password", NewPassword, true, ct);
        var counted = await context.LoginThrottles.AsNoTracking().Where(t => t.LoginId == user.LoginId).Select(t => t.Failures).SingleAsync(ct);
        var keptOthers = await passwords.ChangeAsync(user.Id, here.Id, Password, NewPassword, false, ct);
        var liveAfterKeeping = await context.Sessions.CountAsync(s => s.PrincipalId == user.Id && s.EndedAt == null, ct);
        var endedOthers = await passwords.ChangeAsync(user.Id, here.Id, NewPassword, "and another passphrase", true, ct);
        var live = await context.Sessions.AsNoTracking().Where(s => s.PrincipalId == user.Id && s.EndedAt == null).Select(s => s.Id).ToListAsync(ct);
        var events = await PasswordPolicyTests.SecurityEventsAsync(scope.ServiceProvider, user.Id);

        Assert.Equal(IdentityErrors.PasswordCurrentWrong, wrongCurrent.Error.Code);
        Assert.Equal("current_password", Assert.Single(wrongCurrent.Error.Fields).Field);
        Assert.Equal(1, counted); // like a failed sign-in, towards the lockout
        Assert.True(keptOthers.IsSuccess);
        Assert.Equal(2, liveAfterKeeping);
        Assert.True(endedOthers.IsSuccess);
        Assert.Equal([here.Id], live);
        Assert.DoesNotContain(elsewhere.Id, live);
        Assert.Equal([SecurityEventKinds.PasswordChanged, SecurityEventKinds.PasswordChanged], events.Select(e => e.Kind));
    }

    [Fact]
    [Proves("TF-010 AC3", "ADR-0046")]
    public async Task ADeactivatedPersonCannotSignInTheirSessionsEndAndTheirLinksStopWorking()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"leaver-{Ids.New():N}@acme.test", Password);
        var login = scope.ServiceProvider.GetRequiredService<LoginService>();
        var session = await login.LoginAsync(user.LoginId, Password, ct);
        var recovery = await PasswordPolicyTests.OpenRecoveryAsync(scope.ServiceProvider, user);
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();

        var deactivated = await scope.ServiceProvider.GetRequiredService<UserService>().DeactivateAsync(user, ct);
        var again = await scope.ServiceProvider.GetRequiredService<UserService>().DeactivateAsync(user, ct);
        var resolved = await sessions.ResolveAsync(session.Value.Token!, ct);
        var signIn = await login.LoginAsync(user.LoginId, Password, ct);
        var recovered = await scope.ServiceProvider.GetRequiredService<PasswordService>().RecoverAsync(recovery, NewPassword, ct);
        var events = await PasswordPolicyTests.SecurityEventsAsync(scope.ServiceProvider, user.Id);

        Assert.True(deactivated.IsSuccess);
        Assert.Equal(PrincipalStatus.Disabled, user.Status);
        Assert.Equal(IdentityErrors.UserStatusConflict, again.Error.Code);
        Assert.False(resolved.IsAlive);
        Assert.Equal(IdentityErrors.LoginInvalid, signIn.Error.Code);
        Assert.Equal(IdentityErrors.RecoveryInvalid, recovered.Error.Code);
        Assert.Equal(SecurityEventKinds.UserDeactivated, events[^1].Kind);
    }

    [Fact]
    [Proves("TF-018 AC1", "ADR-0046")]
    public async Task ReactivationRestoresAnAccountOrOpensANewInvitationForOneThatNeverStarted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        var invitations = scope.ServiceProvider.GetRequiredService<InvitationService>();
        var active = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"back-{Ids.New():N}@acme.test", Password);
        var invited = await IdentityFixture.CreateInvitedUserAsync(scope.ServiceProvider, $"never-{Ids.New():N}@acme.test");
        var oldInvitation = await invitations.InviteAsync(invited, ct);
        Assert.True((await users.DeactivateAsync(active, ct)).IsSuccess);
        Assert.True((await users.DeactivateAsync(invited, ct)).IsSuccess);

        var closedInvitation = await invitations.AcceptAsync(oldInvitation, NewPassword, null, ct);
        var activeBack = await users.ReactivateAsync(active, ct);
        var invitedBack = await users.ReactivateAsync(invited, ct);
        var invitedStatus = invited.Status; // before the new invitation is accepted below
        var notDeactivated = await users.ReactivateAsync(active, ct);
        var signIn = await scope.ServiceProvider.GetRequiredService<LoginService>().LoginAsync(active.LoginId, Password, ct);
        var accepted = await invitations.AcceptAsync(invitedBack.Value.InvitationToken!, NewPassword, null, ct);

        Assert.Equal(IdentityErrors.InvitationNotFound, closedInvitation.Error.Code);
        Assert.Equal((PrincipalStatus.Active, null), (activeBack.Value.Principal.Status, activeBack.Value.InvitationToken));
        Assert.Equal(PrincipalStatus.Invited, invitedStatus);
        Assert.NotNull(invitedBack.Value.InvitationToken);
        Assert.Equal(IdentityErrors.UserStatusConflict, notDeactivated.Error.Code);
        Assert.True(signIn.IsSuccess);
        Assert.Equal(PrincipalStatus.Active, accepted.Value.Status);
    }

    [Fact]
    [Proves("ADR-0031", "ADR-0046")]
    public async Task TheLastActiveTenantAdministratorStaysAndNobodyDeactivatesThemselves()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = await fixture.CreateTenantDatabaseAsync($"lastadmin_{Ids.New():N}");
        Guid first, second;
        await using (var setup = fixture.BuildServices(new FakeTimeProvider(), connectionString: tenant))
        await using (var scope = setup.CreateAsyncScope())
        {
            first = (await AdministratorAsync(scope.ServiceProvider, $"first-{Ids.New():N}@acme.test")).Id;
            second = (await AdministratorAsync(scope.ServiceProvider, $"second-{Ids.New():N}@acme.test")).Id;
        }

        await using var asFirst = fixture.BuildServices(new FakeTimeProvider(), connectionString: tenant, configure: services => services.AddScoped<ICurrentPrincipal>(_ => new ActorPrincipal(first)));
        await using var firstScope = asFirst.CreateAsyncScope();
        var context = firstScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var users = firstScope.ServiceProvider.GetRequiredService<UserService>();

        var self = await users.DeactivateAsync(await context.Principals.SingleAsync(p => p.Id == first, ct), ct);
        var colleague = await users.DeactivateAsync(await context.Principals.SingleAsync(p => p.Id == second, ct), ct);
        await using var asSystem = fixture.BuildServices(new FakeTimeProvider(), connectionString: tenant);
        await using var systemScope = asSystem.CreateAsyncScope();
        var last = await systemScope.ServiceProvider.GetRequiredService<UserService>().DeactivateAsync(
            await systemScope.ServiceProvider.GetRequiredService<IdentityDbContext>().Principals.SingleAsync(p => p.Id == first, ct), ct);

        Assert.Equal(IdentityErrors.UserSelf, self.Error.Code);
        Assert.True(colleague.IsSuccess);
        Assert.Equal((ErrorKind.Conflict, IdentityErrors.UserLastAdministrator), (last.Error.Kind, last.Error.Code)); // the seed's administrator is only Invited
    }

    [Fact]
    [Proves("TF-017 AC1", "ADR-0046")]
    public async Task TheOperatorsRecoveryIsForAnActiveTenantAdministratorNamesTheOperatorAndTheReasonAndHandsTheLinkOverUnlogged()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Operator = "operator@control.test";
        const string Reason = "The administrator lost their password (ticket 4711)";
        var tenant = await fixture.CreateTenantDatabaseAsync($"operator_{Ids.New():N}");
        string reader;
        await using (var setup = fixture.BuildServices(new FakeTimeProvider(), connectionString: tenant))
        await using (var scope = setup.CreateAsyncScope())
        {
            var invitation = await IdentityFixture.ReinviteAdministratorAsync(scope.ServiceProvider);
            Assert.True((await scope.ServiceProvider.GetRequiredService<InvitationService>().AcceptAsync(invitation, Password, null, ct)).IsSuccess);
            reader = (await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"reader-{Ids.New():N}@acme.test", Password)).LoginId;
        }

        var handedOver = new CapturingHandover();
        var logs = new CapturingLogs();
        ServiceProvider Recovering(string? loginId, string? reason, bool withPlace = true) => fixture.BuildServices(
            new FakeTimeProvider(),
            connectionString: tenant,
            settings: new Dictionary<string, string?>
            {
                ["Recovery:LoginId"] = loginId,
                ["Recovery:Operator"] = Operator,
                ["Recovery:Reason"] = reason,
            },
            configure: services =>
            {
                services.AddLogging(logging => logging.AddProvider(logs));
                if (withPlace)
                {
                    services.AddSingleton<IOperatorHandover>(handedOver);
                }
            });

        await using var forTheAdministrator = Recovering(IdentityFixture.AdministratorLoginId, Reason);
        await using var forAReader = Recovering(reader, Reason);
        await using var withoutReason = Recovering(IdentityFixture.AdministratorLoginId, " ");
        await using var withoutPlace = Recovering(IdentityFixture.AdministratorLoginId, Reason, withPlace: false);
        var issued = await DatabaseCommands.RunAsync(forTheAdministrator, [DatabaseCommands.RecoverAdministrator], ct);
        var notAnAdministrator = await DatabaseCommands.RunAsync(forAReader, [DatabaseCommands.RecoverAdministrator], ct);
        var noReason = await DatabaseCommands.RunAsync(withoutReason, [DatabaseCommands.RecoverAdministrator], ct);
        var noPlace = await DatabaseCommands.RunAsync(withoutPlace, [DatabaseCommands.RecoverAdministrator], ct);
        var deposited = Assert.Single(handedOver.Items);
        var code = deposited.LinkPath[(deposited.LinkPath.IndexOf('#', StringComparison.Ordinal) + 1)..];
        await using var scopeAfter = forTheAdministrator.CreateAsyncScope();
        var command = (AdministratorRecoveryCommand)scopeAfter.ServiceProvider.GetServices<IUnitCommand>().Single(c => c.Name == DatabaseCommands.RecoverAdministrator);
        var replaced = await command.IssueAsync(ct);
        var stale = await scopeAfter.ServiceProvider.GetRequiredService<PasswordService>().RecoverAsync(code, NewPassword, ct);
        var used = await scopeAfter.ServiceProvider.GetRequiredService<PasswordService>().RecoverAsync(replaced.Value.Token, NewPassword, ct);
        var administrator = await scopeAfter.ServiceProvider.GetRequiredService<IdentityDbContext>().Principals.AsNoTracking().SingleAsync(p => p.LoginId == IdentityFixture.AdministratorLoginId, ct);
        var events = await PasswordPolicyTests.SecurityEventsAsync(scopeAfter.ServiceProvider, administrator.Id);
        var audit = await scopeAfter.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.EventType == IdentityAuditEvents.RecoveryLinkIssued && e.RecordId == administrator.Id).ToListAsync(ct);

        Assert.Equal((0, 1, 1, 1), (issued, notAnAdministrator, noReason, noPlace));
        Assert.Equal((OperatorHandoverItem.Recovery, IdentityFixture.AdministratorLoginId), (deposited.Kind, deposited.Subject));
        Assert.StartsWith("/recovery#", deposited.LinkPath, StringComparison.Ordinal);
        Assert.Equal(IdentityErrors.RecoveryInvalid, stale.Error.Code); // the handed-over link was replaced by the newer one
        Assert.True(used.IsSuccess);
        Assert.Contains(logs.Lines, line => line.Contains("Handed over a recovery link", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains(code, StringComparison.Ordinal) || line.Contains("/recovery#", StringComparison.Ordinal));
        var operatorEvents = events.Where(e => e.Kind == SecurityEventKinds.RecoveryLinkIssued).ToList();
        Assert.Equal(2, operatorEvents.Count);
        Assert.All(operatorEvents, e => Assert.Equal((SecurityEventActors.Operator, Operator, Reason), (e.Actor, e.ActorName, e.Reason)));
        Assert.Equal(2, audit.Count);
        Assert.All(audit, e =>
        {
            Assert.Equal(Reason, e.Reason);
            Assert.Equal(Operator, JsonDocument.Parse(e.Details!).RootElement.GetProperty("operator").GetString());
        });
    }

    /// <summary>An active person with the tenant administrator role for the whole tenant.</summary>
    private static async Task<Principal> AdministratorAsync(IServiceProvider services, string loginId)
    {
        var user = await IdentityFixture.CreateActiveUserAsync(services, loginId, Password);
        Assert.True((await services.GetRequiredService<IRoleAssignments>().AssignAsync(user.Id, SystemRoles.TenantAdministrator, TestContext.Current.CancellationToken)).IsSuccess);
        return user;
    }

    [Fact]
    [Proves("TF-018 AC1", "ADR-0046")]
    public async Task TheSeedHandsTheFirstAdministratorsInvitationOverAndLogsNoLink()
    {
        var ct = TestContext.Current.CancellationToken;
        var handedOver = new CapturingHandover();
        var logs = new CapturingLogs();
        var tenant = await fixture.CreateTenantDatabaseAsync($"seedhandover_{Ids.New():N}");
        var administrator = $"first-{Ids.New():N}@acme.test";
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), administrator, services =>
        {
            services.AddLogging(logging => logging.AddProvider(logs));
            services.AddSingleton<IOperatorHandover>(handedOver);
        }, connectionString: tenant);

        var seeded = await DatabaseCommands.RunAsync(provider, [DatabaseCommands.Seed], ct);
        var deposited = Assert.Single(handedOver.Items);
        var token = deposited.LinkPath[(deposited.LinkPath.IndexOf('#', StringComparison.Ordinal) + 1)..];
        await using var scope = provider.CreateAsyncScope();
        var accepted = await scope.ServiceProvider.GetRequiredService<InvitationService>().AcceptAsync(token, Password, null, ct);

        Assert.Equal(0, seeded);
        Assert.Equal((OperatorHandoverItem.Invitation, administrator), (deposited.Kind, deposited.Subject));
        Assert.StartsWith("/invitations#", deposited.LinkPath, StringComparison.Ordinal);
        Assert.True(accepted.IsSuccess);
        Assert.Contains(logs.Lines, line => line.Contains("Handed over an invitation", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains(token, StringComparison.Ordinal) || line.Contains("/invitations#", StringComparison.Ordinal));
    }

    private sealed class ActorPrincipal(Guid id) : ICurrentPrincipal
    {
        public Guid Id => id;
    }

    /// <summary>The fleet's handover place as a command sees it: what was deposited, in order.</summary>
    private sealed class CapturingHandover : IOperatorHandover
    {
        public List<OperatorHandoverItem> Items { get; } = [];

        public bool IsAvailable => true;

        public Task DepositAsync(OperatorHandoverItem item, CancellationToken cancellationToken)
        {
            Items.Add(item);
            return Task.CompletedTask;
        }
    }

    /// <summary>Every log line the command wrote, formatted as a provider would write it.</summary>
    private sealed class CapturingLogs : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue(formatter(state, exception));
        }
    }
}
