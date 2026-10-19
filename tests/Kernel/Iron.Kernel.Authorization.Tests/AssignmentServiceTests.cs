using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>Assignments made through the API against PostgreSQL (TF-033, ADR-0031): each rule with its error code.</summary>
[Collection(AuthorizationCollection.Name)]
public class AssignmentServiceTests(AuthorizationFixture fixture)
{
    private static readonly string[] GranterPermissions =
    [
        AuthorizationPermissions.AssignmentsRead.Name,
        AuthorizationPermissions.AssignmentsManage.Name,
        AuthorizationPermissions.RolesRead.Name,
        AuthorizationPermissions.UnitsRead.Name,
    ];

    [Fact]
    public async Task AnAdministratorGrantsAndRemovesARoleAtAUnitWithTheAuditAndTheCacheClearedAtOnce()
    {
        var (_, sales, _) = await TreeAsync();
        var reader = await RoleAsync([AuthorizationPermissions.UnitsRead.Name]);
        var person = fixture.Principals.Add();
        await using var scope = await AdministratorAsync();
        var assignments = scope.Get<AssignmentService>();
        var grants = scope.Get<AccessGrants>();
        Assert.Empty(await grants.PermissionsAtAsync(person, AccessScope.Unit(sales.Id), TestContext.Current.CancellationToken));

        var granted = await assignments.GrantAsync(person, reader.Id, RoleScope.Unit(sales.Id), TestContext.Current.CancellationToken);

        Assert.True(granted.IsSuccess, granted.IsFailure ? granted.Error.Message : null);
        Assert.Equal([sales.Id], granted.Value.UnitPath.Select(step => step.Id).Skip(1));
        Assert.Equal("Sales", granted.Value.UnitPath[^1].Name);
        Assert.Equal(reader.Code, granted.Value.RoleCode);
        Assert.Contains(AuthorizationPermissions.UnitsRead.Name, await grants.PermissionsAtAsync(person, AccessScope.Unit(sales.Id), TestContext.Current.CancellationToken));
        var assigned = Assert.Single(scope.Audit.Events, e => e.Action == AuthorizationAuditEvents.RoleAssigned);
        Assert.Equal(AuditOutcome.Success, assigned.Outcome);
        Assert.Equal($"unit:{sales.Id:N}", assigned.Details?["scope"]);
        Assert.Equal(person.ToString(), assigned.Details?["principal_id"]);

        var again = await assignments.GrantAsync(person, reader.Id, RoleScope.Unit(sales.Id), TestContext.Current.CancellationToken);
        Assert.Equal(granted.Value.Id, again.Value.Id);

        var removed = await assignments.RemoveAsync(granted.Value.Id, TestContext.Current.CancellationToken);

        Assert.True(removed.IsSuccess);
        Assert.Empty(await grants.PermissionsAtAsync(person, AccessScope.Unit(sales.Id), TestContext.Current.CancellationToken));
        Assert.Single(scope.Audit.Events, e => e.Action == AuthorizationAuditEvents.RoleAssigned);
        Assert.Single(scope.Audit.Events, e => e.Action == AuthorizationAuditEvents.RoleUnassigned && e.RecordId == granted.Value.Id);
    }

    [Fact]
    public async Task AGrantNeedsEveryPermissionOfTheRoleAtTheScope()
    {
        var (root, sales, marketing) = await TreeAsync();
        var east = await UnitAsync(sales.Id, "East");
        var reader = await RoleAsync([AuthorizationPermissions.UnitsRead.Name]);
        var administrator = await SystemRoleIdAsync(SystemRoles.TenantAdministrator);
        var person = fixture.Principals.Add();
        await using var scope = fixture.Scope(await GranterAsync(RoleScope.Unit(sales.Id, includeChildren: true)));
        var assignments = scope.Get<AssignmentService>();

        var within = await assignments.GrantAsync(person, reader.Id, RoleScope.Unit(east.Id), TestContext.Current.CancellationToken);
        var subtree = await assignments.GrantAsync(person, reader.Id, RoleScope.Unit(sales.Id, includeChildren: true), TestContext.Current.CancellationToken);
        var more = await assignments.GrantAsync(person, administrator, RoleScope.Unit(sales.Id), TestContext.Current.CancellationToken);
        var sibling = await assignments.GrantAsync(person, reader.Id, RoleScope.Unit(marketing.Id), TestContext.Current.CancellationToken);
        var above = await assignments.GrantAsync(person, reader.Id, RoleScope.Unit(root.Id, includeChildren: true), TestContext.Current.CancellationToken);
        var tenant = await assignments.GrantAsync(person, reader.Id, RoleScope.Tenant, TestContext.Current.CancellationToken);

        Assert.True(within.IsSuccess);
        Assert.True(subtree.IsSuccess);
        Assert.Equal(AuthorizationErrors.AssignmentEscalation, more.Error.Code);
        Assert.Equal(ErrorKind.Forbidden, more.Error.Kind);
        Assert.Contains(AuthorizationPermissions.RolesManage.Name, more.Error.Message, StringComparison.Ordinal);
        Assert.Contains(scope.Audit.Events, e => e.Action == AuthorizationAuditEvents.RoleAssigned && e.Outcome == AuditOutcome.Denied);
        Assert.Equal(PermissionErrors.PermissionDenied, sibling.Error.Code);
        Assert.Equal(PermissionErrors.PermissionDenied, above.Error.Code);
        Assert.Equal(PermissionErrors.PermissionDenied, tenant.Error.Code);
    }

    [Fact]
    public async Task AGrantAtAUnitAloneDoesNotReachItsChildren()
    {
        var (_, sales, _) = await TreeAsync();
        var reader = await RoleAsync([AuthorizationPermissions.UnitsRead.Name]);
        var person = fixture.Principals.Add();
        await using var scope = fixture.Scope(await GranterAsync(RoleScope.Unit(sales.Id)));
        var assignments = scope.Get<AssignmentService>();

        var unit = await assignments.GrantAsync(person, reader.Id, RoleScope.Unit(sales.Id), TestContext.Current.CancellationToken);
        var withChildren = await assignments.GrantAsync(person, reader.Id, RoleScope.Unit(sales.Id, includeChildren: true), TestContext.Current.CancellationToken);

        Assert.True(unit.IsSuccess);
        Assert.Equal(PermissionErrors.PermissionDenied, withChildren.Error.Code);
    }

    [Fact]
    public async Task ARemovalNeedsTheManagePermissionAtTheAssignmentsScope()
    {
        var (_, sales, marketing) = await TreeAsync();
        var reader = await RoleAsync([AuthorizationPermissions.UnitsRead.Name]);
        var person = fixture.Principals.Add();
        Guid elsewhere;
        await using (var setup = await AdministratorAsync())
        {
            elsewhere = (await setup.Get<AssignmentService>().GrantAsync(person, reader.Id, RoleScope.Unit(marketing.Id), TestContext.Current.CancellationToken)).Value.Id;
        }

        await using var scope = fixture.Scope(await GranterAsync(RoleScope.Unit(sales.Id, includeChildren: true)));
        var removed = await scope.Get<AssignmentService>().RemoveAsync(elsewhere, TestContext.Current.CancellationToken);

        Assert.Equal(PermissionErrors.PermissionDenied, removed.Error.Code);
    }

    [Fact]
    public async Task ARoleIsGrantedOnlyToAPrincipalOfItsTypeAndOnlyToAPrincipalThatExists()
    {
        var reader = await RoleAsync([AuthorizationPermissions.UnitsRead.Name]);
        var administrator = await SystemRoleIdAsync(SystemRoles.TenantAdministrator);
        var service = fixture.Principals.Add(PrincipalKind.Service);
        await using var scope = await AdministratorAsync();
        var assignments = scope.Get<AssignmentService>();

        var wrongType = await assignments.GrantAsync(service, reader.Id, RoleScope.Tenant, TestContext.Current.CancellationToken);
        var nobody = await assignments.GrantAsync(Ids.New(), reader.Id, RoleScope.Tenant, TestContext.Current.CancellationToken);
        var anyType = await assignments.GrantAsync(service, administrator, RoleScope.Tenant, TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationErrors.AssignmentPrincipalType, wrongType.Error.Code);
        Assert.Equal("role_id", Assert.Single(wrongType.Error.Fields).Field);
        Assert.Equal(AuthorizationErrors.PrincipalNotFound, nobody.Error.Code);
        Assert.True(anyType.IsSuccess);
    }

    [Fact]
    public async Task TheLastTenantAdministratorCannotBeRemoved()
    {
        var tenant = await fixture.CreateTenantDatabaseAsync($"guard_{Ids.New():N}");
        var first = fixture.Principals.Add();
        var second = fixture.Principals.Add();
        await using (var setup = fixture.Scope(SystemPrincipal.SystemId, connectionString: tenant))
        {
            await RoleServiceTests.AssignAsync(setup.Scope, first, SystemRoles.TenantAdministrator);
        }

        var administrator = await SystemRoleIdAsync(SystemRoles.TenantAdministrator, tenant);
        await using var asFirst = fixture.Scope(first, connectionString: tenant);
        var assignments = asFirst.Get<AssignmentService>();
        var own = await asFirst.Get<AuthorizationDbContext>().RoleAssignments.Where(a => a.PrincipalId == first).Select(a => a.Id).SingleAsync(TestContext.Current.CancellationToken);
        var root = await OrganisationUnitServiceTests.RootAsync(asFirst.Scope);

        var alone = await assignments.RemoveAsync(own, TestContext.Current.CancellationToken);
        var atUnit = await assignments.GrantAsync(second, administrator, RoleScope.Unit(root.Id), TestContext.Current.CancellationToken);
        var removedAtUnit = await assignments.RemoveAsync(atUnit.Value.Id, TestContext.Current.CancellationToken);
        var colleague = await assignments.GrantAsync(second, administrator, RoleScope.Tenant, TestContext.Current.CancellationToken);
        var handedOver = await assignments.RemoveAsync(own, TestContext.Current.CancellationToken);
        await using var asSecond = fixture.Scope(second, connectionString: tenant);
        var lastAgain = await asSecond.Get<AssignmentService>().RemoveAsync(colleague.Value.Id, TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationErrors.AssignmentLastAdministrator, alone.Error.Code);
        Assert.Equal(ErrorKind.Conflict, alone.Error.Kind);
        Assert.True(removedAtUnit.IsSuccess);
        Assert.True(handedOver.IsSuccess);
        Assert.Equal(AuthorizationErrors.AssignmentLastAdministrator, lastAgain.Error.Code);
    }

    [Fact]
    public async Task AReaderSeesTheAssignmentsAtTheUnitsTheyReadAndNoOthers()
    {
        var (_, sales, marketing) = await TreeAsync();
        var east = await UnitAsync(sales.Id, "East");
        var reader = await RoleAsync([AuthorizationPermissions.UnitsRead.Name]);
        var person = fixture.Principals.Add();
        var expected = new List<Guid>();
        await using (var setup = await AdministratorAsync())
        {
            var assignments = setup.Get<AssignmentService>();
            expected.Add((await assignments.GrantAsync(person, reader.Id, RoleScope.Unit(sales.Id), TestContext.Current.CancellationToken)).Value.Id);
            expected.Add((await assignments.GrantAsync(person, reader.Id, RoleScope.Unit(east.Id, includeChildren: true), TestContext.Current.CancellationToken)).Value.Id);
            Assert.True((await assignments.GrantAsync(person, reader.Id, RoleScope.Unit(marketing.Id), TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await assignments.GrantAsync(person, reader.Id, RoleScope.Tenant, TestContext.Current.CancellationToken)).IsSuccess);
        }

        await using var scope = fixture.Scope(await GranterAsync(RoleScope.Unit(sales.Id, includeChildren: true)));
        var visible = await scope.Get<AssignmentService>().VisibleAsync(TestContext.Current.CancellationToken);
        var seen = await visible.Where(a => a.PrincipalId == person).Select(a => a.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected.Order(), seen.Order());
    }

    private async Task<(OrganisationUnit Root, OrganisationUnit Sales, OrganisationUnit Marketing)> TreeAsync()
    {
        await using var scope = fixture.Scope(SystemPrincipal.SystemId);
        var root = await OrganisationUnitServiceTests.RootAsync(scope.Scope);
        var units = scope.Get<OrganisationUnitService>();
        return (root, await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Sales"), await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Marketing"));
    }

    private async Task<OrganisationUnit> UnitAsync(Guid parent, string name)
    {
        await using var scope = fixture.Scope(SystemPrincipal.SystemId);
        return await OrganisationUnitServiceTests.CreateAsync(scope.Get<OrganisationUnitService>(), parent, name);
    }

    private async Task<Role> RoleAsync(IReadOnlyList<string> permissions)
    {
        await using var scope = fixture.Scope(SystemPrincipal.SystemId);
        var context = scope.Get<AuthorizationDbContext>();
        var role = new Role(RoleServiceTests.Code(), "Test role", string.Empty, isSystem: false, PrincipalKind.User, permissions);
        context.Roles.Add(role);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return role;
    }

    /// <summary>A principal who may grant at one scope: the assignment permissions and the unit and role reads, no more.</summary>
    private async Task<Guid> GranterAsync(RoleScope scope)
    {
        var granter = fixture.Principals.Add();
        var role = await RoleAsync(GranterPermissions);
        await using var setup = fixture.Scope(SystemPrincipal.SystemId);
        await RoleServiceTests.AssignAsync(setup.Scope, granter, role.Code, scope);
        return granter;
    }

    private async Task<TestScope> AdministratorAsync()
    {
        var administrator = fixture.Principals.Add();
        await using (var setup = fixture.Scope(SystemPrincipal.SystemId))
        {
            await RoleServiceTests.AssignAsync(setup.Scope, administrator, SystemRoles.TenantAdministrator);
        }

        return fixture.Scope(administrator);
    }

    private async Task<Guid> SystemRoleIdAsync(string code, string? connectionString = null)
    {
        await using var scope = fixture.Scope(SystemPrincipal.SystemId, connectionString: connectionString);
        return await scope.Get<AuthorizationDbContext>().Roles.Where(r => r.Code == code).Select(r => r.Id).SingleAsync(TestContext.Current.CancellationToken);
    }
}
