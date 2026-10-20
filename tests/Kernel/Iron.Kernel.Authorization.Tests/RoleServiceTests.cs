using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>Tenant roles against PostgreSQL (TF-032, ADR-0031): created, changed and deleted with their audit, system roles read-only.</summary>
[Collection(AuthorizationCollection.Name)]
public class RoleServiceTests(AuthorizationFixture fixture)
{
    private static readonly string[] Reading = [AuthorizationPermissions.RolesRead.Name, AuthorizationPermissions.UnitsRead.Name];

    [Fact]
    [Untraced("The audit of role changes with the permissions before and after is in the module README only; no criterion of TF-032 or ADR states it.")]
    public async Task ARoleIsCreatedChangedAndDeletedWithThePermissionsBeforeAndAfterInTheAudit()
    {
        await using var administrator = await AsAdministratorAsync();
        var roles = administrator.Get<RoleService>();

        var created = await roles.CreateAsync(Code(), new RoleInput("Unit reader", "Reads units.", PrincipalKind.User, [AuthorizationPermissions.UnitsRead.Name]), TestContext.Current.CancellationToken);
        var changed = await roles.UpdateAsync(created.Value.Id, new RoleInput("Unit and role reader", "Reads units and roles.", PrincipalKind.User, Reading), TestContext.Current.CancellationToken);
        var deleted = await roles.DeleteAsync(created.Value.Id, TestContext.Current.CancellationToken);

        Assert.True(created.IsSuccess);
        Assert.Equal(Reading.Order(StringComparer.Ordinal), changed.Value.Permissions);
        Assert.True(deleted.IsSuccess);
        var audit = administrator.Audit;
        var create = Assert.Single(audit.Events, e => e.Action == AuthorizationAuditEvents.RoleCreated);
        Assert.Equal(string.Empty, create.Details?["permissions_before"]);
        Assert.Equal(AuthorizationPermissions.UnitsRead.Name, create.Details?["permissions_after"]);
        var change = Assert.Single(audit.Events, e => e.Action == AuthorizationAuditEvents.RoleChanged);
        Assert.Equal(AuthorizationPermissions.UnitsRead.Name, change.Details?["permissions_before"]);
        Assert.Equal(string.Join(',', Reading.Order(StringComparer.Ordinal)), change.Details?["permissions_after"]);
        Assert.Equal("name,description", change.Details?["fields"]);
        var delete = Assert.Single(audit.Events, e => e.Action == AuthorizationAuditEvents.RoleDeleted);
        Assert.Equal(string.Join(',', Reading.Order(StringComparer.Ordinal)), delete.Details?["permissions_before"]);
    }

    [Fact]
    [Proves("TF-032 AC2", "ADR-0031")]
    public async Task ASystemRoleIsReadOnly()
    {
        await using var administrator = await AsAdministratorAsync();
        var roles = administrator.Get<RoleService>();
        var readOnly = await administrator.Get<AuthorizationDbContext>().Roles.AsNoTracking().SingleAsync(r => r.Code == SystemRoles.ReadOnly, TestContext.Current.CancellationToken);

        var change = await roles.UpdateAsync(readOnly.Id, new RoleInput("Mine now", string.Empty, PrincipalKind.User, []), TestContext.Current.CancellationToken);
        var delete = await roles.DeleteAsync(readOnly.Id, TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationErrors.RoleSystem, change.Error.Code);
        Assert.Equal(ErrorKind.Conflict, change.Error.Kind);
        Assert.Equal(AuthorizationErrors.RoleSystem, delete.Error.Code);
    }

    [Fact]
    [Proves("ADR-0031")]
    public async Task ARoleStillAssignedIsNotDeleted()
    {
        await using var administrator = await AsAdministratorAsync();
        var roles = administrator.Get<RoleService>();
        var role = await roles.CreateAsync(Code(), new RoleInput("Assigned", string.Empty, PrincipalKind.User, Reading), TestContext.Current.CancellationToken);
        await using (var system = fixture.Scope(SystemPrincipal.SystemId))
        {
            Assert.True((await system.Get<IRoleAssignments>().AssignAsync(Ids.New(), role.Value.Code, TestContext.Current.CancellationToken)).IsSuccess);
        }

        var delete = await roles.DeleteAsync(role.Value.Id, TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationErrors.RoleInUse, delete.Error.Code);
    }

    [Fact]
    [Proves("ADR-0031")]
    public async Task UnknownPermissionsATakenCodeAndARoleForBothTypesAreRefused()
    {
        await using var administrator = await AsAdministratorAsync();
        var roles = administrator.Get<RoleService>();
        var code = Code();
        await roles.CreateAsync(code, new RoleInput("First", string.Empty, PrincipalKind.User, []), TestContext.Current.CancellationToken);

        var unknown = await roles.CreateAsync(Code(), new RoleInput("Unknown", string.Empty, PrincipalKind.User, ["procurement.orders.approve"]), TestContext.Current.CancellationToken);
        var taken = await roles.CreateAsync(code, new RoleInput("Second", string.Empty, PrincipalKind.User, []), TestContext.Current.CancellationToken);
        var both = await roles.CreateAsync(Code(), new RoleInput("Both", string.Empty, PrincipalKind.Any, []), TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationErrors.RolePermissionUnknown, unknown.Error.Code);
        Assert.Equal("permissions", Assert.Single(unknown.Error.Fields).Field);
        Assert.Equal(AuthorizationErrors.RoleCodeTaken, taken.Error.Code);
        Assert.Equal(ErrorKind.Conflict, taken.Error.Kind);
        Assert.Equal(AuthorizationErrors.RolePrincipalType, both.Error.Code);
    }

    [Fact]
    [Proves("ADR-0031")]
    public async Task ARoleIsSavedOnlyWithPermissionsItsEditorHoldsTenantWide()
    {
        var editor = Ids.New();
        await using (var setup = fixture.Scope(SystemPrincipal.SystemId))
        {
            var editorRole = Code();
            var context = setup.Get<AuthorizationDbContext>();
            context.Roles.Add(new Role(editorRole, "Role editor", string.Empty, isSystem: false, PrincipalKind.User, [AuthorizationPermissions.RolesRead.Name, AuthorizationPermissions.RolesManage.Name]));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await AssignAsync(setup.Scope, editor, editorRole);
            await AssignAsync(setup.Scope, editor, SystemRoles.TenantAdministrator, RoleScope.Unit((await OrganisationUnitServiceTests.RootAsync(setup.Scope)).Id, includeChildren: true));
        }

        await using var scope = fixture.Scope(editor);
        var roles = scope.Get<RoleService>();

        var within = await roles.CreateAsync(Code(), new RoleInput("Role reader", string.Empty, PrincipalKind.User, [AuthorizationPermissions.RolesRead.Name]), TestContext.Current.CancellationToken);
        var beyond = await roles.CreateAsync(Code(), new RoleInput("Unit manager", string.Empty, PrincipalKind.User, [AuthorizationPermissions.UnitsManage.Name]), TestContext.Current.CancellationToken);
        var changedBeyond = await roles.UpdateAsync(within.Value.Id, new RoleInput("Role reader", string.Empty, PrincipalKind.User, [AuthorizationPermissions.AssignmentsManage.Name]), TestContext.Current.CancellationToken);

        Assert.True(within.IsSuccess);
        Assert.Equal(AuthorizationErrors.RoleEscalation, beyond.Error.Code);
        Assert.Equal(ErrorKind.Forbidden, beyond.Error.Kind);
        Assert.Contains(AuthorizationPermissions.UnitsManage.Name, beyond.Error.Message, StringComparison.Ordinal);
        Assert.Equal(AuthorizationErrors.RoleEscalation, changedBeyond.Error.Code);
    }

    internal static string Code() => $"r-{Ids.New():N}";

    internal static async Task AssignAsync(AsyncServiceScope scope, Guid principal, string role, RoleScope? roleScope = null)
    {
        var result = await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, role, roleScope ?? RoleScope.Tenant, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }

    private async Task<TestScope> AsAdministratorAsync()
    {
        var administrator = Ids.New();
        await using (var setup = fixture.Scope(SystemPrincipal.SystemId))
        {
            await AssignAsync(setup.Scope, administrator, SystemRoles.TenantAdministrator);
        }

        return fixture.Scope(administrator);
    }
}
