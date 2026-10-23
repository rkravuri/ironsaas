using System.Text.Json;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Changes.Contracts;
using Iron.Kernel.Changes.Data;
using Iron.Kernel.Changes.Services;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data.Retention;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Settings.Contracts;
using Iron.Kernel.Settings.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Changes.Tests;

/// <summary>
/// Four-eyes on sensitive configuration (S3 T8, TF-068, ADR-0052): a save in a listed area waits, another administrator
/// approves and it applies with both people audited, and nobody approves their own change, while signed in as someone
/// else, acting for someone or as a break-glass account.
/// </summary>
[Collection(ChangesCollection.Name)]
public class FourEyesTests(ChangesFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-068 AC1", "ADR-0052")]
    public async Task ARoleSavedWhileFourEyesIsOnForRolesWaitsWithItsBeforeAndAfterAndNothingApplies()
    {
        var (provider, actor, _, requester, _) = await SetupAsync([ConfigurationAreas.Roles]);
        await using var _ = provider;
        var role = await RoleAsync(provider, actor, ["authz.roles.read"]);

        actor.As(requester);
        var held = await UpdateRoleAsync(provider, role, ["authz.roles.read", "authz.units.read"]);

        Assert.Equal(ErrorKind.Held, held.Error.Kind);
        Assert.Equal(ChangesErrors.Held, held.Error.Code);
        var change = await ChangeAsync(provider, (Guid)held.Error.Extensions["change_id"]);
        Assert.Equal((PendingChangeStates.Pending, ConfigurationAreas.Roles, RoleService.UpdateKind, requester), (change.Status, change.Area, change.Kind, change.RequestedById));
        Assert.Equal(["authz.roles.read"], change.Before.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(["authz.roles.read", "authz.units.read"], change.After.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(["authz.roles.read"], (await FindRoleAsync(provider, role)).Permissions);
        Assert.Equal(requester, (await EventAsync(provider, ChangesAuditEvents.ChangeRequested, change.Id)).ActorId);
    }

    [Fact]
    [Proves("TF-068 AC2", "ADR-0052")]
    public async Task TheRequesterCannotApproveTheirOwnChangeAndTheAttemptIsRecorded()
    {
        var (provider, actor, _, requester, _) = await SetupAsync([ConfigurationAreas.Roles]);
        await using var _ = provider;
        var role = await RoleAsync(provider, actor, ["authz.roles.read"]);
        actor.As(requester);
        var id = (Guid)(await UpdateRoleAsync(provider, role, ["authz.units.read"])).Error.Extensions["change_id"];

        var own = await DecideAsync(provider, c => c.ApproveAsync(id, null, null, Cancel));
        var reject = await DecideAsync(provider, c => c.RejectAsync(id, "I changed my mind.", null, Cancel));

        Assert.Equal(ChangesErrors.Own, own.Error.Code);
        Assert.Equal(ChangesErrors.Own, reject.Error.Code);
        Assert.Equal(PendingChangeStates.Pending, (await ChangeAsync(provider, id)).Status);
        Assert.Equal(["authz.roles.read"], (await FindRoleAsync(provider, role)).Permissions);
        var refused = await EventAsync(provider, ChangesAuditEvents.ApprovalRefused, id);
        Assert.Equal((AuditOutcome.Denied, AuditSeverity.High, requester), (refused.Outcome, refused.Severity, refused.ActorId));
    }

    [Fact]
    [Proves("TF-068 AC3", "ADR-0052")]
    public async Task AnotherAdministratorApprovesTheChangeAppliesAndBothPeopleAreAudited()
    {
        var (provider, actor, _, requester, approver) = await SetupAsync([ConfigurationAreas.Roles]);
        await using var _ = provider;
        var role = await RoleAsync(provider, actor, ["authz.roles.read"]);
        actor.As(requester);
        var id = (Guid)(await UpdateRoleAsync(provider, role, ["authz.roles.read", "authz.units.read"])).Error.Extensions["change_id"];

        actor.As(approver);
        var approved = await DecideAsync(provider, c => c.ApproveAsync(id, "Matches the request in the ticket.", null, Cancel));

        Assert.True(approved.IsSuccess, approved.IsFailure ? approved.Error.ToString() : null);
        Assert.Equal(["authz.roles.read", "authz.units.read"], (await FindRoleAsync(provider, role)).Permissions);
        var change = await ChangeAsync(provider, id);
        Assert.Equal((PendingChangeStates.Applied, approver), (change.Status, change.DecidedById));
        var decided = await EventAsync(provider, ChangesAuditEvents.ChangeApproved, id);
        var details = JsonDocument.Parse(decided.Details!).RootElement;
        Assert.Equal(approver, decided.ActorId);
        Assert.Equal(requester.ToString("D"), details.GetProperty("requested_by").GetString());
        Assert.Equal(approver.ToString("D"), details.GetProperty("decided_by").GetString());
        Assert.Equal(approver, (await EventAsync(provider, AuthorizationAuditEvents.RoleChanged, role)).ActorId);

        // The role's field history names the approved change it came from (TF-062, ADR-0051).
        var history = await EventAsync(provider, AuditEvents.RecordChanged, role);
        Assert.Equal(approver, history.ActorId);
        Assert.Contains(id.ToString("D"), history.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [Proves("TF-068 AC2", "ADR-0049", "ADR-0052")]
    [InlineData("impersonating", ChangesErrors.Impersonation)]
    [InlineData("break_glass", ChangesErrors.BreakGlass)]
    [InlineData("acting_for", ChangesErrors.ActingFor)]
    public async Task NobodyApprovesWhileSignedInAsSomeoneElseActingForSomeoneOrAsABreakGlassAccount(string how, string refusal)
    {
        var (provider, actor, _, requester, approver) = await SetupAsync([ConfigurationAreas.Roles]);
        await using var _ = provider;
        var role = await RoleAsync(provider, actor, ["authz.roles.read"]);
        actor.As(requester);
        var id = (Guid)(await UpdateRoleAsync(provider, role, ["authz.units.read"])).Error.Extensions["change_id"];

        actor.As(approver);
        switch (how)
        {
            case "impersonating":
                actor.ImpersonatorId = Ids.New();
                actor.ImpersonatorName = "Someone else";
                break;
            case "break_glass":
                actor.IsBreakGlass = true;
                break;
            default:
                actor.ActingForId = requester;
                break;
        }

        var refused = await DecideAsync(provider, c => c.ApproveAsync(id, null, null, Cancel));

        Assert.Equal(refusal, refused.Error.Code);
        Assert.Equal(PendingChangeStates.Pending, (await ChangeAsync(provider, id)).Status);
        Assert.Equal(["authz.roles.read"], (await FindRoleAsync(provider, role)).Permissions);
        Assert.Equal(AuditOutcome.Denied, (await EventAsync(provider, ChangesAuditEvents.ApprovalRefused, id)).Outcome);
    }

    [Fact]
    [Proves("TF-068 AC1", "ADR-0052")]
    public async Task ARejectedChangeNeverAppliesAndARejectionNeedsAReason()
    {
        var (provider, actor, _, requester, approver) = await SetupAsync([ConfigurationAreas.Roles]);
        await using var _ = provider;
        var role = await RoleAsync(provider, actor, ["authz.roles.read"]);
        actor.As(requester);
        var id = (Guid)(await UpdateRoleAsync(provider, role, ["authz.units.read"])).Error.Extensions["change_id"];

        actor.As(approver);
        var silent = await DecideAsync(provider, c => c.RejectAsync(id, " ", null, Cancel));
        var rejected = await DecideAsync(provider, c => c.RejectAsync(id, "The ticket asks for read access only.", null, Cancel));
        var late = await DecideAsync(provider, c => c.ApproveAsync(id, null, null, Cancel));

        Assert.Equal(ChangesErrors.ReasonRequired, silent.Error.Code);
        Assert.True(rejected.IsSuccess);
        Assert.Equal(ChangesErrors.NotPending, late.Error.Code);
        var change = await ChangeAsync(provider, id);
        Assert.Equal((PendingChangeStates.Rejected, "The ticket asks for read access only."), (change.Status, change.DecisionReason));
        Assert.Equal(["authz.roles.read"], (await FindRoleAsync(provider, role)).Permissions);
        Assert.Equal(approver, (await EventAsync(provider, ChangesAuditEvents.ChangeRejected, id)).ActorId);
    }

    [Fact]
    [Proves("ADR-0052")]
    public async Task AChangeNobodyDecidesExpiresAfterTheTenantsPeriodAndCanNoLongerApply()
    {
        var (provider, actor, time, requester, approver) = await SetupAsync([ConfigurationAreas.Roles]);
        await using var _ = provider;
        var role = await RoleAsync(provider, actor, ["authz.roles.read"]);
        actor.As(requester);
        var id = (Guid)(await UpdateRoleAsync(provider, role, ["authz.units.read"])).Error.Extensions["change_id"];

        time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        int expired;
        await using (var scope = provider.CreateAsyncScope())
        {
            expired = await scope.ServiceProvider.GetRequiredService<PendingChanges>().ExpireAsync(Cancel);
        }

        actor.As(approver);
        var late = await DecideAsync(provider, c => c.ApproveAsync(id, null, null, Cancel));

        Assert.True(expired >= 1);
        Assert.Equal(PendingChangeStates.Expired, (await ChangeAsync(provider, id)).Status);
        Assert.Equal(ChangesErrors.Expired, late.Error.Code);
        Assert.Equal(["authz.roles.read"], (await FindRoleAsync(provider, role)).Permissions);
        Assert.Equal(SystemPrincipal.SystemId, (await EventAsync(provider, ChangesAuditEvents.ChangeExpired, id)).ActorId);
    }

    [Fact]
    [Proves("ADR-0052")]
    public async Task AChangeWhoseSubjectChangedMeanwhileStopsForANewReviewThenAppliesOnTheNextApproval()
    {
        var (provider, actor, _, requester, approver) = await SetupAsync([ConfigurationAreas.Roles]);
        await using var _ = provider;
        var role = await RoleAsync(provider, actor, ["authz.roles.read"]);
        actor.As(requester);
        var id = (Guid)(await UpdateRoleAsync(provider, role, ["authz.roles.read", "authz.units.read"])).Error.Extensions["change_id"];

        // Something else changes the role meanwhile: a change approved before this one.
        var meanwhile = await ArrangeAsync(provider, actor, services => services.GetRequiredService<RoleService>().UpdateAsync(role, new RoleInput("Four-eyes probe", string.Empty, PrincipalKind.User, ["authz.roles.read", "authz.assignments.read"]), Cancel));
        Assert.True(meanwhile.IsSuccess);

        actor.As(approver);
        var stopped = await DecideAsync(provider, c => c.ApproveAsync(id, null, null, Cancel));
        var waiting = await ChangeAsync(provider, id);
        var unchanged = await FindRoleAsync(provider, role);
        var second = await DecideAsync(provider, c => c.ApproveAsync(id, "Reviewed against the new values.", null, Cancel));

        Assert.Equal(ChangesErrors.Stale, stopped.Error.Code);
        Assert.Equal((PendingChangeStates.Pending, 2), (waiting.Status, waiting.Reviews));
        Assert.Equal(["authz.assignments.read", "authz.roles.read"], waiting.Before.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains("authz.assignments.read", waiting.RestartNote, StringComparison.Ordinal);
        Assert.Equal(["authz.assignments.read", "authz.roles.read"], unchanged.Permissions);
        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.ToString() : null);
        Assert.Equal(["authz.roles.read", "authz.units.read"], (await FindRoleAsync(provider, role)).Permissions);
        Assert.Equal(approver, (await EventAsync(provider, ChangesAuditEvents.ChangeRestarted, id)).ActorId);
    }

    [Fact]
    [Proves("ADR-0052")]
    public async Task OneChangeWaitsPerSubjectAndItsRequesterMayWithdrawIt()
    {
        var (provider, actor, _, requester, approver) = await SetupAsync([ConfigurationAreas.Roles]);
        await using var _ = provider;
        var role = await RoleAsync(provider, actor, ["authz.roles.read"]);
        actor.As(requester);
        var id = (Guid)(await UpdateRoleAsync(provider, role, ["authz.units.read"])).Error.Extensions["change_id"];

        actor.As(approver);
        var second = await UpdateRoleAsync(provider, role, ["authz.assignments.read"]);
        var notTheirs = await DecideAsync(provider, c => c.WithdrawAsync(id, Cancel));
        actor.As(requester);
        var withdrawn = await DecideAsync(provider, c => c.WithdrawAsync(id, Cancel));
        var again = await UpdateRoleAsync(provider, role, ["authz.assignments.read"]);

        Assert.Equal(ChangesErrors.AlreadyPending, second.Error.Code);
        Assert.Equal(id, (Guid)second.Error.Extensions["change_id"]);
        Assert.Equal(ChangesErrors.NotYours, notTheirs.Error.Code);
        Assert.True(withdrawn.IsSuccess);
        Assert.Equal(PendingChangeStates.Withdrawn, (await ChangeAsync(provider, id)).Status);
        Assert.Equal(ErrorKind.Held, again.Error.Kind);
    }

    [Fact]
    [Proves("TF-068 AC1", "TF-068 AC3", "ADR-0052")]
    public async Task AGrantWaitsAndOnlyAnAdministratorWhoHoldsTheGrantPermissionAtItsUnitApprovesIt()
    {
        var (provider, actor, _, requester, _) = await SetupAsync([ConfigurationAreas.Roles]);
        await using var _ = provider;
        var (unit, other) = await UnitsAsync(provider, actor);
        var unitAdministrator = await ScopedAdministratorAsync(provider, actor, unit);
        var otherAdministrator = await ScopedAdministratorAsync(provider, actor, other);
        var person = fixture.Principals.Add(homeUnit: unit, name: "Pat Person");
        var role = await RoleAsync(provider, actor, ["authz.units.read"]);

        actor.As(requester);
        Result<AssignmentView> held;
        await using (var scope = provider.CreateAsyncScope())
        {
            held = await scope.ServiceProvider.GetRequiredService<AssignmentService>().GrantAsync(person, role, RoleScope.Unit(unit), Cancel);
        }

        var id = (Guid)held.Error.Extensions["change_id"];
        var change = await ChangeAsync(provider, id);
        actor.As(otherAdministrator);
        var elsewhere = await DecideAsync(provider, c => c.ApproveAsync(id, null, null, Cancel));
        actor.As(unitAdministrator);
        var there = await DecideAsync(provider, c => c.ApproveAsync(id, null, null, Cancel));

        Assert.Equal((AuthorizationPermissions.AssignmentsManage.Name, unit), (change.Permission, change.ScopeUnitId));
        Assert.Equal(PermissionErrors.PermissionDenied, elsewhere.Error.Code);
        Assert.True(there.IsSuccess, there.IsFailure ? there.Error.ToString() : null);
        await using var check = provider.CreateAsyncScope();
        Assert.Contains("authz.units.read", await check.ServiceProvider.GetRequiredService<IRoleAssignments>().PermissionsOfAsync(person, AccessScope.Unit(unit), Cancel));
    }

    [Fact]
    [Proves("TF-068 AC1", "TF-068 AC3", "ADR-0052")]
    public async Task TheMfaAndPasswordPoliciesAndTheSigningPolicyWaitAndApplyOnApproval()
    {
        var (provider, actor, _, requester, approver) = await SetupAsync([ConfigurationAreas.MfaAndPasswordPolicies, ConfigurationAreas.SigningPolicy]);
        await using var _ = provider;

        actor.As(requester);
        var length = await SetAsync(provider, IdentitySettings.PasswordMinimumLength.Key, JsonSerializer.SerializeToElement(16));
        var mfa = await SetAsync(provider, IdentitySettings.MfaRequiredFor.Key, JsonSerializer.SerializeToElement("everyone"));
        var signing = await SetAsync(provider, ComplianceSettings.LaterSecondFactor.Key, JsonSerializer.SerializeToElement(true));
        var session = await SetAsync(provider, IdentitySettings.SessionIdleMinutes.Key, JsonSerializer.SerializeToElement(30));

        Assert.All([length, mfa, signing], held => Assert.Equal(ErrorKind.Held, held.Error.Kind));
        Assert.True(session.IsSuccess, "A setting outside the listed areas saves at once.");
        Assert.Equal(12, await ValueAsync(provider, IdentitySettings.PasswordMinimumLength));
        var lengthChange = await ChangeAsync(provider, (Guid)length.Error.Extensions["change_id"]);
        Assert.Equal(JsonValueKind.Null, lengthChange.Before.GetProperty("value").ValueKind);
        Assert.Equal(16, lengthChange.After.GetProperty("value").GetInt32());

        actor.As(approver);
        foreach (var held in new[] { length, mfa, signing })
        {
            var approved = await DecideAsync(provider, c => c.ApproveAsync((Guid)held.Error.Extensions["change_id"], null, null, Cancel));
            Assert.True(approved.IsSuccess, approved.IsFailure ? approved.Error.ToString() : null);
        }

        Assert.Equal(16, await ValueAsync(provider, IdentitySettings.PasswordMinimumLength));
        Assert.Equal("everyone", await ValueAsync(provider, IdentitySettings.MfaRequiredFor));
        Assert.True(await ValueAsync(provider, ComplianceSettings.LaterSecondFactor));
        await ResetAsync(provider, actor, [IdentitySettings.PasswordMinimumLength.Key, IdentitySettings.MfaRequiredFor.Key, ComplianceSettings.LaterSecondFactor.Key, IdentitySettings.SessionIdleMinutes.Key]);
    }

    [Fact]
    [Proves("TF-068 AC1", "TF-068 AC3", "ADR-0052")]
    public async Task RetentionAndLegalHoldsWaitAndApplyOnApproval()
    {
        var (provider, actor, _, requester, approver) = await SetupAsync([ConfigurationAreas.Retention, ConfigurationAreas.LegalHolds]);
        await using var _ = provider;
        var record = Ids.New();

        actor.As(requester);
        Result<RetentionClassView> retention;
        Result<LegalHold> hold;
        await using (var scope = provider.CreateAsyncScope())
        {
            retention = await scope.ServiceProvider.GetRequiredService<RetentionPolicy>().ChooseAsync(RetentionClasses.Session.Code, 500, "Matches the security policy.", Cancel);
            hold = await scope.ServiceProvider.GetRequiredService<LegalHolds>().PlaceAsync(LegalHoldScopes.Record, "sample_order", record, "Dispute 42.", Cancel);
        }

        Assert.Equal(ErrorKind.Held, retention.Error.Kind);
        Assert.Equal(ErrorKind.Held, hold.Error.Kind);
        await using (var scope = provider.CreateAsyncScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            Assert.False(await audit.LegalHolds.AnyAsync(h => h.RecordId == record, Cancel));
        }

        actor.As(approver);
        Assert.True((await DecideAsync(provider, c => c.ApproveAsync((Guid)retention.Error.Extensions["change_id"], null, null, Cancel))).IsSuccess);
        Assert.True((await DecideAsync(provider, c => c.ApproveAsync((Guid)hold.Error.Extensions["change_id"], null, null, Cancel))).IsSuccess);

        await using var check = provider.CreateAsyncScope();
        var placed = await check.ServiceProvider.GetRequiredService<AuditDbContext>().LegalHolds.AsNoTracking().SingleAsync(h => h.RecordId == record, Cancel);
        Assert.Equal(("Dispute 42.", approver), (placed.Reason, placed.CreatedBy));
        Assert.Equal(500, await check.ServiceProvider.GetRequiredService<RetentionPolicy>().DaysAsync(RetentionClasses.Session, Cancel));
    }

    [Fact]
    [Proves("TF-068 AC1", "ADR-0052")]
    public async Task WhileAnyAreaIsListedTheFourEyesSettingItselfWaitsForASecondAdministrator()
    {
        var (provider, actor, _, requester, approver) = await SetupAsync([ConfigurationAreas.LegalHolds]);
        await using var _ = provider;

        actor.As(requester);
        var off = await SetAsync(provider, ChangesSettings.Areas.Key, JsonSerializer.SerializeToElement(Array.Empty<string>()));
        var stillOn = ChangesSettings.AreasOf(await ValueAsync(provider, ChangesSettings.Areas));
        actor.As(approver);
        var approved = await DecideAsync(provider, c => c.ApproveAsync((Guid)off.Error.Extensions["change_id"], null, null, Cancel));
        var nowOff = ChangesSettings.AreasOf(await ValueAsync(provider, ChangesSettings.Areas));
        actor.As(requester);
        var on = await SetAsync(provider, ChangesSettings.Areas.Key, JsonSerializer.SerializeToElement(new[] { ConfigurationAreas.Roles }));

        Assert.Equal(ErrorKind.Held, off.Error.Kind);
        Assert.Equal([ConfigurationAreas.LegalHolds], stillOn);
        Assert.True(approved.IsSuccess);
        Assert.Empty(nowOff);
        Assert.True(on.IsSuccess, "With no area listed, one administrator turns four-eyes on.");
    }

    [Fact]
    [Proves("TF-068 AC3", "TF-063 AC1", "ADR-0052")]
    public async Task InASignedAreaTheApprovalIsAnElectronicSignatureMeaningApproved()
    {
        var (provider, actor, _, requester, approver) = await SetupAsync([ConfigurationAreas.Roles], [ConfigurationAreas.Roles]);
        await using var _ = provider;
        var role = await RoleAsync(provider, actor, ["authz.roles.read"]);
        actor.As(requester);
        var id = (Guid)(await UpdateRoleAsync(provider, role, ["authz.units.read"])).Error.Extensions["change_id"];
        var signer = (StandInSigner)provider.GetRequiredService<ISignerAuthentication>();

        actor.As(approver);
        signer.Refusal = Error.Forbidden(ComplianceErrors.Invalid, "That did not confirm it is you.");
        var unsigned = await DecideAsync(provider, c => c.ApproveAsync(id, null, SigningCredentials.None, Cancel));
        signer.Refusal = null;
        var signed = await DecideAsync(provider, c => c.ApproveAsync(id, "Approved as asked.", new SigningCredentials(null, "password", null, null, null), Cancel));

        Assert.Equal(ComplianceErrors.Invalid, unsigned.Error.Code);
        Assert.True(signed.IsSuccess, signed.IsFailure ? signed.Error.ToString() : null);
        var change = await ChangeAsync(provider, id);
        await using var scope = provider.CreateAsyncScope();
        var signature = Assert.Single(await scope.ServiceProvider.GetRequiredService<ISignatures>().ForRecordAsync(PendingChange.RecordType, id, Cancel));
        Assert.Equal((change.SignatureId, SignatureMeanings.Approved, true, false), ((Guid?)signature.Id, signature.Meaning, signature.HashMatches, signature.RecordChanged));
    }

    [Fact]
    [Proves("TF-068 AC2", "TF-248 AC1", "ADR-0052")]
    public async Task AnApprovalNeedsARecentConfirmationThatItIsTheApprover()
    {
        var recent = new SwitchableRecentAuthentication();
        var (provider, actor, _, requester, approver) = await SetupAsync([ConfigurationAreas.Roles], recent: recent);
        await using var _ = provider;
        var role = await RoleAsync(provider, actor, ["authz.roles.read"]);
        actor.As(requester);
        var id = (Guid)(await UpdateRoleAsync(provider, role, ["authz.units.read"])).Error.Extensions["change_id"];

        actor.As(approver);
        recent.Stale = true;
        var stale = await DecideAsync(provider, c => c.ApproveAsync(id, null, null, Cancel));

        Assert.Equal(RecentAuthentication.ErrorCode, stale.Error.Code);
        Assert.Equal(PendingChangeStates.Pending, (await ChangeAsync(provider, id)).Status);
    }

    [Fact]
    [Proves("TF-068 AC1", "ADR-0052")]
    public async Task ThePeopleSeeTheirOwnChangesAndTheOnesTheyMayDecide()
    {
        var (provider, actor, _, requester, approver) = await SetupAsync([ConfigurationAreas.Roles]);
        await using var _ = provider;
        var role = await RoleAsync(provider, actor, ["authz.roles.read"]);
        actor.As(requester);
        var id = (Guid)(await UpdateRoleAsync(provider, role, ["authz.units.read"])).Error.Extensions["change_id"];

        var mine = await ListAsync(provider, ChangeView.Mine);
        var notMine = await ListAsync(provider, ChangeView.ToApprove);
        actor.As(approver);
        var toApprove = await ListAsync(provider, ChangeView.ToApprove);

        Assert.Contains(mine, i => i.Change.Id == id && !i.MayDecide);
        Assert.DoesNotContain(notMine, i => i.Change.Id == id);
        Assert.Contains(toApprove, i => i.Change.Id == id && i.MayDecide);
    }

    private async Task<(ServiceProvider Provider, TestActor Actor, FakeTimeProvider Time, Guid Requester, Guid Approver)> SetupAsync(string[] areas, string[]? signed = null, SwitchableRecentAuthentication? recent = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var actor = new TestActor();
        var provider = fixture.BuildServices(time, actor, new StandInSigner(actor), recent);
        var requester = await fixture.AdministratorAsync(provider, "Rae Requester");
        var approver = await fixture.AdministratorAsync(provider, "Abe Approver");
        _setup = await fixture.AdministratorAsync(provider, "Sam Setup");
        await ChangesFixture.FourEyesAsync(provider, actor, areas, signed);
        return (provider, actor, time, requester, approver);
    }

    private Guid _setup;

    /// <summary>
    /// Arranges what a test needs as an administrator of its own, in a scope that applies a change already approved, so
    /// the setup itself never waits for a second administrator.
    /// </summary>
    private async Task<T> ArrangeAsync<T>(IServiceProvider provider, TestActor actor, Func<IServiceProvider, Task<T>> arrange)
    {
        var acting = actor.Id;
        actor.As(_setup);
        try
        {
            await using var scope = provider.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ChangeApplication>().Applying = Ids.New();
            return await arrange(scope.ServiceProvider);
        }
        finally
        {
            actor.Id = acting;
        }
    }

    private Task<Guid> RoleAsync(IServiceProvider provider, TestActor actor, string[] permissions) =>
        ArrangeAsync(provider, actor, async services =>
        {
            var created = await services.GetRequiredService<RoleService>().CreateAsync($"four-eyes-{Ids.New().ToString("N")[^16..]}", new RoleInput("Four-eyes probe", string.Empty, PrincipalKind.User, permissions), Cancel);
            Assert.True(created.IsSuccess, created.IsFailure ? created.Error.ToString() : null);
            return created.Value.Id;
        });

    private static async Task<Result<Iron.Kernel.Authorization.Data.Role>> UpdateRoleAsync(IServiceProvider provider, Guid role, string[] permissions)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RoleService>().UpdateAsync(role, new RoleInput("Four-eyes probe", string.Empty, PrincipalKind.User, permissions), Cancel);
    }

    private static async Task<Iron.Kernel.Authorization.Data.Role> FindRoleAsync(IServiceProvider provider, Guid role)
    {
        await using var scope = provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<RoleService>().FindAsync(role, Cancel))!;
    }

    private static async Task<PendingChange> ChangeAsync(IServiceProvider provider, Guid id)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ChangesDbContext>().Changes.AsNoTracking().SingleAsync(c => c.Id == id, Cancel);
    }

    private static async Task<T> DecideAsync<T>(IServiceProvider provider, Func<PendingChanges, Task<T>> decide)
    {
        await using var scope = provider.CreateAsyncScope();
        return await decide(scope.ServiceProvider.GetRequiredService<PendingChanges>());
    }

    private static async Task<IReadOnlyList<ChangeItem>> ListAsync(IServiceProvider provider, ChangeView view)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PendingChanges>().ListAsync(view, null, Cancel);
    }

    private static async Task<AuditEventRecord> EventAsync(IServiceProvider provider, string type, Guid recordId)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.EventType == type && e.RecordId == recordId).OrderByDescending(e => e.CreatedAt).FirstAsync(Cancel);
    }

    private static async Task<Result> SetAsync(IServiceProvider provider, string key, JsonElement value)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SettingsWriter>().SetAsync(key, SettingPlace.Tenant, value, Cancel);
    }

    private static async Task<T> ValueAsync<T>(IServiceProvider provider, Setting<T> setting)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SettingsRefresher>().RefreshOnceAsync(Cancel);
        return await scope.ServiceProvider.GetRequiredService<ISettings>().GetAsync(setting, SettingContext.Tenant, Cancel);
    }

    private static async Task ResetAsync(IServiceProvider provider, TestActor actor, string[] keys)
    {
        actor.Id = SystemPrincipal.SystemId;
        await using var scope = provider.CreateAsyncScope();
        foreach (var key in keys)
        {
            await scope.ServiceProvider.GetRequiredService<SettingsWriter>().ResetAsync(key, SettingPlace.Tenant, Cancel);
        }
    }

    private Task<(Guid Unit, Guid Other)> UnitsAsync(IServiceProvider provider, TestActor actor) =>
        ArrangeAsync(provider, actor, async services =>
        {
            var units = services.GetRequiredService<OrganisationUnitService>();
            var root = (await services.GetRequiredService<IOrganisationUnits>().RootAsync(Cancel))!.Id;
            var first = await units.CreateAsync(root, $"Four-eyes {Ids.New().ToString("N")[^10..]}", $"fe-{Ids.New().ToString("N")[^9..]}", KernelOrganisationKinds.Unit, Cancel);
            var second = await units.CreateAsync(root, $"Four-eyes {Ids.New().ToString("N")[^10..]}", $"fe-{Ids.New().ToString("N")[^9..]}", KernelOrganisationKinds.Unit, Cancel);
            Assert.True(first.IsSuccess && second.IsSuccess, first.IsFailure ? first.Error.ToString() : second.IsFailure ? second.Error.ToString() : null);
            return (first.Value.Id, second.Value.Id);
        });

    /// <summary>A person who may grant roles at one unit only, and holds what they grant there.</summary>
    private Task<Guid> ScopedAdministratorAsync(IServiceProvider provider, TestActor actor, Guid unit) =>
        ArrangeAsync(provider, actor, async services =>
        {
            var id = fixture.Principals.Add(homeUnit: unit, name: "Unit Administrator");
            var role = await services.GetRequiredService<RoleService>().CreateAsync($"unit-admin-{Ids.New().ToString("N")[^16..]}", new RoleInput("Unit administrator", string.Empty, PrincipalKind.User, [AuthorizationPermissions.AssignmentsManage.Name, "authz.units.read"]), Cancel);
            Assert.True(role.IsSuccess, role.IsFailure ? role.Error.ToString() : null);
            var assigned = await services.GetRequiredService<IRoleAssignments>().AssignAsync(id, role.Value.Code, RoleScope.Unit(unit), Cancel);
            Assert.True(assigned.IsSuccess, assigned.IsFailure ? assigned.Error.ToString() : null);
            return id;
        });
}
