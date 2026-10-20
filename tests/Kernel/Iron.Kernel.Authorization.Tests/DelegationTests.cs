using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>
/// Delegations of duties against PostgreSQL (TF-037, ADR-0037): each refusal with its code, an administrator for someone
/// else, ending early, and acting for the delegator with exactly their current rights for the delegated duties.
/// </summary>
[Collection(AuthorizationCollection.Name)]
public class DelegationTests(AuthorizationFixture fixture)
{
    private static readonly DateTimeOffset Now = new FakeTimeProvider().GetUtcNow();
    private static readonly string Approval = DutyProbeModule.Approval.Code;
    private static readonly string Receiving = DutyProbeModule.Receiving.Code;
    private static readonly string Evaluation = DutyProbeModule.Evaluation.Code;

    [Fact]
    [Proves("ADR-0037")]
    public async Task APersonDelegatesTheirOwnDutiesForAPeriodWithAReasonAndTheChangeIsAudited()
    {
        var (ada, grace) = (fixture.Principals.Add(), fixture.Principals.Add());
        await using var scope = fixture.Scope(ada);

        var created = await CreateAsync(scope, null, grace, [Approval, Receiving]);

        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
        Assert.Equal((ada, grace, DelegationStatus.Scheduled), (created.Value.Delegator.Id, created.Value.Delegate.Id, created.Value.Status));
        Assert.Equal(["approval", "receiving"], created.Value.Duties.Select(d => d.Name));
        var audited = Assert.Single(scope.Audit.Events, e => e.Action == AuthorizationAuditEvents.DelegationCreated);
        Assert.Equal((ada, created.Value.Id, "Annual leave."), (audited.PrincipalId, audited.RecordId, audited.Reason));
    }

    [Fact]
    [Proves("ADR-0037")]
    public async Task ADelegationToOneselfToOrFromAServiceAccountOrForAPeriodThatHasEndedIsRefusedEachWithItsCode()
    {
        var ada = fixture.Principals.Add();
        var robot = fixture.Principals.Add(PrincipalKind.Service);
        await using var scope = fixture.Scope(ada);

        Assert.Equal(AuthorizationErrors.DelegationSelf, (await CreateAsync(scope, null, ada, [Approval])).Error.Code);
        Assert.Equal(AuthorizationErrors.DelegationServiceAccount, (await CreateAsync(scope, null, robot, [Approval])).Error.Code);
        Assert.Equal(AuthorizationErrors.DelegationPeriod, (await CreateAsync(scope, null, fixture.Principals.Add(), [Approval], Now.AddDays(-3), Now.AddDays(-1))).Error.Code);
        Assert.Equal(AuthorizationErrors.DelegationDutyUnknown, (await CreateAsync(scope, null, fixture.Principals.Add(), ["probe.unheard_of"])).Error.Code);
    }

    [Fact]
    [Proves("ADR-0037")]
    public async Task TheSameDelegatorAndDutyInOverlappingPeriodsIsRefusedAndAllSharesEveryDuty()
    {
        var (ada, grace, linus) = (fixture.Principals.Add(), fixture.Principals.Add(), fixture.Principals.Add());
        await using var scope = fixture.Scope(ada);
        Assert.True((await CreateAsync(scope, null, grace, [Approval], Now.AddDays(1), Now.AddDays(10))).IsSuccess);

        Assert.Equal(AuthorizationErrors.DelegationOverlap, (await CreateAsync(scope, null, linus, [Approval], Now.AddDays(5), Now.AddDays(15))).Error.Code);
        Assert.Equal(AuthorizationErrors.DelegationOverlap, (await CreateAsync(scope, null, linus, [KernelDuties.AllCode], Now.AddDays(5), Now.AddDays(15))).Error.Code);

        // Another duty in the same period, and the same duty after the first ends, are both fine.
        Assert.True((await CreateAsync(scope, null, linus, [Receiving], Now.AddDays(5), Now.AddDays(15))).IsSuccess);
        Assert.True((await CreateAsync(scope, null, linus, [Approval], Now.AddDays(10), Now.AddDays(20))).IsSuccess);
    }

    [Fact]
    [Proves("TF-037 AC2", "ADR-0037")]
    public async Task ACircleOfAnyLengthInAnOverlappingPeriodIsRefused()
    {
        var (a, b, c) = (fixture.Principals.Add(), fixture.Principals.Add(), fixture.Principals.Add());
        await using (var scope = fixture.Scope(a))
        {
            Assert.True((await CreateAsync(scope, null, b, [Approval])).IsSuccess);
        }

        // B to A while A to B (TF-037 AC2), with another duty so the onward rule does not decide it first.
        await using (var scope = fixture.Scope(b))
        {
            Assert.Equal(AuthorizationErrors.DelegationCycle, (await CreateAsync(scope, null, a, [Receiving])).Error.Code);
            Assert.True((await CreateAsync(scope, null, c, [Receiving])).IsSuccess);
        }

        // C to A closes A, B, C into a circle of three.
        await using (var scope = fixture.Scope(c))
        {
            Assert.Equal(AuthorizationErrors.DelegationCycle, (await CreateAsync(scope, null, a, [Evaluation])).Error.Code);

            // In a period none of the others counts in, there is no circle.
            Assert.True((await CreateAsync(scope, null, a, [Evaluation], Now.AddDays(30), Now.AddDays(40))).IsSuccess);
        }
    }

    [Fact]
    [Proves("ADR-0037")]
    public async Task DelegatedRightsCannotBeDelegatedAgainNeitherWhileActingNorAsADutyHeldByDelegation()
    {
        var (ada, grace, linus) = (fixture.Principals.Add(), fixture.Principals.Add(), fixture.Principals.Add());
        await using (var scope = fixture.Scope(ada))
        {
            Assert.True((await CreateAsync(scope, null, grace, [Approval], Now.AddMinutes(-5), Now.AddDays(5))).IsSuccess);
        }

        await using (var holding = fixture.Scope(grace))
        {
            Assert.Equal(AuthorizationErrors.DelegationOnward, (await CreateAsync(holding, null, linus, [Approval])).Error.Code);
            Assert.Equal(AuthorizationErrors.DelegationOnward, (await CreateAsync(holding, null, linus, [KernelDuties.AllCode])).Error.Code);
        }

        await using var acting = fixture.ActingScope(grace, ada);
        Assert.Equal(AuthorizationErrors.DelegationOnward, (await CreateAsync(acting, null, linus, [Receiving])).Error.Code);
    }

    [Fact]
    [Proves("ADR-0037")]
    public async Task AnAdministratorDelegatesForSomeoneWithTheManagePermissionAtTheirHomeUnitAndNobodyElseDoes()
    {
        var unit = await UnitAsync("Delegation desk");
        var ada = fixture.Principals.Add(homeUnit: unit);
        var grace = fixture.Principals.Add();
        var administrator = await HolderAsync([AuthorizationPermissions.DelegationsManage.Name], RoleScope.Unit(unit));
        var stranger = fixture.Principals.Add();

        await using (var scope = fixture.Scope(stranger))
        {
            Assert.Equal(PermissionErrors.PermissionDenied, (await CreateAsync(scope, ada, grace, [Approval])).Error.Code);
        }

        await using (var scope = fixture.Scope(administrator))
        {
            var created = await CreateAsync(scope, ada, grace, [Approval]);
            Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
            Assert.Equal((ada, administrator), (created.Value.Delegator.Id, created.Value.CreatedBy));
            var managed = await scope.Get<DelegationService>().ManagedAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(managed);
            Assert.Equal([created.Value.Id], await managed.Select(d => d.Id).ToListAsync(TestContext.Current.CancellationToken));
        }

        await using (var scope = fixture.Scope(stranger))
        {
            Assert.Null(await scope.Get<DelegationService>().ManagedAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    [Proves("ADR-0037")]
    public async Task TheDelegatorTheDelegateOrAnAdministratorEndsADelegationEarlyAndAStrangerCannot()
    {
        var (ada, grace, stranger) = (fixture.Principals.Add(), fixture.Principals.Add(), fixture.Principals.Add());
        Guid id;
        await using (var scope = fixture.Scope(ada))
        {
            id = (await CreateAsync(scope, null, grace, [Approval], Now.AddMinutes(-5), Now.AddDays(5))).Value.Id;
        }

        await using (var scope = fixture.Scope(stranger))
        {
            Assert.Equal(PermissionErrors.PermissionDenied, (await scope.Get<DelegationService>().EndAsync(id, "Not mine.", "test", TestContext.Current.CancellationToken)).Error.Code);
        }

        await using (var scope = fixture.Scope(grace))
        {
            var ended = await scope.Get<DelegationService>().EndAsync(id, "Back early.", "test", TestContext.Current.CancellationToken);
            Assert.Equal((DelegationStatus.Ended, "Back early."), (ended.Value.Status, ended.Value.EndReason));
            Assert.Single(scope.Audit.Events, e => e.Action == AuthorizationAuditEvents.DelegationEnded && e.RecordId == id);
            Assert.Equal(AuthorizationErrors.DelegationNotRunning, (await scope.Get<DelegationService>().EndAsync(id, null, "test", TestContext.Current.CancellationToken)).Error.Code);
        }
    }

    [Fact]
    [Proves("ADR-0037")]
    public async Task ADelegateActingForTheDelegatorHoldsExactlyTheDelegatorsRightsForTheDutiesAtTheDelegatorsScopes()
    {
        var unit = await UnitAsync("Acting desk");
        var ada = await HolderAsync([AuthorizationPermissions.UnitsRead.Name, AuthorizationPermissions.RolesRead.Name], RoleScope.Tenant);
        await GrantAsync(ada, [AuthorizationPermissions.AssignmentsRead.Name], RoleScope.Unit(unit));
        var grace = await HolderAsync([AuthorizationPermissions.UnitsManage.Name], RoleScope.Tenant);
        await using (var scope = fixture.Scope(ada))
        {
            Assert.True((await CreateAsync(scope, null, grace, [Approval, Evaluation], Now.AddMinutes(-5), Now.AddDays(5))).IsSuccess);
        }

        await using var acting = fixture.ActingScope(grace, ada);
        var grants = acting.Get<AccessGrants>();
        var tenant = await grants.PermissionsAtAsync(grace, AccessScope.Tenant, TestContext.Current.CancellationToken);
        var atUnit = await grants.PermissionsAtAsync(grace, AccessScope.Unit(unit), TestContext.Current.CancellationToken);

        // Approval is units.read, which Ada holds tenant-wide; evaluation is assignments.read, which she holds at the unit only.
        Assert.Equal([AuthorizationPermissions.UnitsRead.Name], tenant.Order());
        Assert.Equal([AuthorizationPermissions.AssignmentsRead.Name, AuthorizationPermissions.UnitsRead.Name], atUnit.Order());
        // Ada's roles.read is not delegated, and Grace's own units.manage does not mix in.
        Assert.DoesNotContain(AuthorizationPermissions.RolesRead.Name, atUnit);
        Assert.DoesNotContain(AuthorizationPermissions.UnitsManage.Name, atUnit);

        // As herself, Grace has her own rights and none of Ada's.
        await using var herself = fixture.Scope(grace);
        Assert.Equal([AuthorizationPermissions.UnitsManage.Name], await herself.Get<AccessGrants>().PermissionsAtAsync(grace, AccessScope.Tenant, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0037")]
    public async Task AllIsEverythingTheDelegatorHoldsAndADelegatorWhoLosesARoleTakesItFromTheDelegateAtOnce()
    {
        var ada = await HolderAsync([AuthorizationPermissions.UnitsRead.Name, AuthorizationPermissions.RolesRead.Name], RoleScope.Tenant);
        var grace = fixture.Principals.Add();
        await using (var scope = fixture.Scope(ada))
        {
            Assert.True((await CreateAsync(scope, null, grace, [KernelDuties.AllCode], Now.AddMinutes(-5), Now.AddDays(5))).IsSuccess);
        }

        await using (var acting = fixture.ActingScope(grace, ada))
        {
            Assert.Equal([AuthorizationPermissions.RolesRead.Name, AuthorizationPermissions.UnitsRead.Name], (await acting.Get<AccessGrants>().PermissionsAtAsync(grace, AccessScope.Tenant, TestContext.Current.CancellationToken)).Order());
        }

        // Ada's role is taken away while the delegation runs: Grace loses it on her next request.
        await using (var setup = fixture.Scope(SystemPrincipal.SystemId))
        {
            var context = setup.Get<AuthorizationDbContext>();
            context.RoleAssignments.RemoveRange(context.RoleAssignments.Where(a => a.PrincipalId == ada));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            setup.Get<AccessGrants>().Forget(ada);
        }

        await using var after = fixture.ActingScope(grace, ada);
        Assert.Empty(await after.Get<AccessGrants>().PermissionsAtAsync(grace, AccessScope.Tenant, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0037")]
    public async Task WhenTheDelegationEndsActingGivesNothingAndTheEvaluatorDenies()
    {
        var ada = await HolderAsync([AuthorizationPermissions.UnitsRead.Name], RoleScope.Tenant);
        var grace = fixture.Principals.Add();
        var time = new FakeTimeProvider(Now);
        await using (var scope = fixture.Scope(ada, time))
        {
            Assert.True((await CreateAsync(scope, null, grace, [Approval], Now.AddMinutes(-5), Now.AddHours(1))).IsSuccess);
        }

        time.Advance(TimeSpan.FromHours(2));
        await using var acting = fixture.ActingScope(grace, ada, time);
        var decision = await acting.Get<IPermissionEvaluator>().EvaluateAsync(new AccessRequest(grace, AuthorizationPermissions.UnitsRead, "GET /api/v1/organisation-units", "/api/v1/organisation-units"), TestContext.Current.CancellationToken);
        Assert.False(decision.Allowed);
    }

    private static Task<Result<DelegationView>> CreateAsync(TestScope scope, Guid? delegator, Guid delegateTo, IReadOnlyList<string> duties, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        scope.Get<DelegationService>().CreateAsync(new DelegationInput(delegator, delegateTo, duties, from ?? Now.AddDays(1), to ?? Now.AddDays(8), "Annual leave."), "POST /api/v1/delegations", TestContext.Current.CancellationToken);

    private async Task<Guid> UnitAsync(string name)
    {
        await using var scope = fixture.Scope(SystemPrincipal.SystemId);
        var root = await OrganisationUnitServiceTests.RootAsync(scope.Scope);
        return (await OrganisationUnitServiceTests.CreateAsync(scope.Get<OrganisationUnitService>(), root.Id, name)).Id;
    }

    private async Task<Guid> HolderAsync(IReadOnlyList<string> permissions, RoleScope scope)
    {
        var principal = fixture.Principals.Add();
        await GrantAsync(principal, permissions, scope);
        return principal;
    }

    private async Task GrantAsync(Guid principal, IReadOnlyList<string> permissions, RoleScope scope)
    {
        await using var setup = fixture.Scope(SystemPrincipal.SystemId);
        var context = setup.Get<AuthorizationDbContext>();
        var role = new Role(RoleServiceTests.Code(), "Delegation test role", string.Empty, isSystem: false, PrincipalKind.User, permissions);
        context.Roles.Add(role);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RoleServiceTests.AssignAsync(setup.Scope, principal, role.Code, scope);
    }
}
