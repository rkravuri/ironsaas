using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>TF-032: roles export from one tenant's database and import into another's, with a dry run that shows the diff first.</summary>
[Collection(AuthorizationCollection.Name)]
public class RoleImportTests(AuthorizationFixture fixture)
{
    [Fact]
    public async Task RolesRoundTripFromOneTenantToAnotherWithADryRunFirst()
    {
        var bravo = await fixture.CreateTenantDatabaseAsync($"bravo_{Ids.New():N}");
        await using var source = await AdministratorAsync(fixture.ConnectionString);
        await using var target = await AdministratorAsync(bravo);
        var sourceRoles = source.Get<RoleService>();
        var targetRoles = target.Get<RoleService>();
        var auditor = Code("auditor");
        var planner = Code("planner");
        Assert.True((await sourceRoles.CreateAsync(auditor, new RoleInput("Auditor", "Reads roles.", PrincipalKind.User, [AuthorizationPermissions.RolesRead.Name]), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await sourceRoles.CreateAsync(planner, new RoleInput("Planner", "Shapes the tree.", PrincipalKind.User, [AuthorizationPermissions.UnitsRead.Name, AuthorizationPermissions.UnitsManage.Name]), TestContext.Current.CancellationToken)).IsSuccess);
        var export = Only(await sourceRoles.ExportAsync(TestContext.Current.CancellationToken), auditor, planner);

        var dryRun = await targetRoles.ImportAsync(export, dryRun: true, TestContext.Current.CancellationToken);
        Assert.False(dryRun.Value.Applied);
        Assert.Equal([auditor, planner], dryRun.Value.Created);
        Assert.Null(await FindAsync(bravo, auditor));

        var applied = await targetRoles.ImportAsync(export, dryRun: false, TestContext.Current.CancellationToken);
        Assert.True(applied.Value.Applied);
        Assert.Equal(["authz.units.manage", "authz.units.read"], (await FindAsync(bravo, planner))?.Permissions);
        Assert.Equal(2, target.Audit.Events.Count(e => e.Action == AuthorizationAuditEvents.RoleCreated && e.Reason == "import"));

        var again = await targetRoles.ImportAsync(export, dryRun: true, TestContext.Current.CancellationToken);
        Assert.Equal([auditor, planner], again.Value.Unchanged);
        Assert.Empty(again.Value.Created);

        var planned = export.Roles.Single(r => r.Code == planner);
        var changedExport = export with { Roles = [export.Roles.Single(r => r.Code == auditor), planned with { Permissions = [AuthorizationPermissions.UnitsRead.Name, AuthorizationPermissions.RolesRead.Name] }] };
        var change = Assert.Single((await targetRoles.ImportAsync(changedExport, dryRun: true, TestContext.Current.CancellationToken)).Value.Changed);
        Assert.Equal(planner, change.Code);
        Assert.Equal([AuthorizationPermissions.RolesRead.Name], change.Added);
        Assert.Equal([AuthorizationPermissions.UnitsManage.Name], change.Removed);
    }

    [Fact]
    public async Task UnknownPermissionsAreShownInTheDryRunAndRefuseTheApplyAndSystemRolesAreNeverImported()
    {
        await using var target = await AdministratorAsync(fixture.ConnectionString);
        var roles = target.Get<RoleService>();
        var buyer = Code("buyer");
        var document = new RoleExport(RoleExport.CurrentFormat,
        [
            new RoleDocument(buyer, "Buyer", string.Empty, PrincipalKind.User, [AuthorizationPermissions.RolesRead.Name, "procurement.orders.create"]),
            new RoleDocument(SystemRoles.TenantAdministrator, "Mine", string.Empty, PrincipalKind.User, []),
        ]);

        var dryRun = await roles.ImportAsync(document, dryRun: true, TestContext.Current.CancellationToken);
        var apply = await roles.ImportAsync(document, dryRun: false, TestContext.Current.CancellationToken);

        var unknown = Assert.Single(dryRun.Value.Unknown);
        Assert.Equal(buyer, unknown.Code);
        Assert.Equal(["procurement.orders.create"], unknown.Permissions);
        Assert.Equal([SystemRoles.TenantAdministrator], dryRun.Value.SkippedSystem);
        Assert.Equal(AuthorizationErrors.ImportUnknownPermissions, apply.Error.Code);
        Assert.Null(await FindAsync(fixture.ConnectionString, buyer));
    }

    [Fact]
    public async Task AnImportCannotGiveARolePermissionsTheImporterLacksTenantWide()
    {
        var importer = Ids.New();
        var managerRole = Code("role-manager");
        await using (var setup = fixture.Scope(SystemPrincipal.SystemId))
        {
            var context = setup.Get<AuthorizationDbContext>();
            context.Roles.Add(new Role(managerRole, "Role manager", string.Empty, isSystem: false, PrincipalKind.User, [AuthorizationPermissions.RolesRead.Name, AuthorizationPermissions.RolesManage.Name]));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await RoleServiceTests.AssignAsync(setup.Scope, importer, managerRole);
        }

        await using var scope = fixture.Scope(importer);
        var document = new RoleExport(RoleExport.CurrentFormat, [new RoleDocument(Code("granter"), "Granter", string.Empty, PrincipalKind.User, [AuthorizationPermissions.AssignmentsManage.Name])]);

        var apply = await scope.Get<RoleService>().ImportAsync(document, dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationErrors.RoleEscalation, apply.Error.Code);
    }

    private static string Code(string name) => $"{name}-{Ids.New():N}"[..30];

    private static RoleExport Only(RoleExport export, params string[] codes) => export with { Roles = export.Roles.Where(r => codes.Contains(r.Code)).ToList() };

    private async Task<TestScope> AdministratorAsync(string connectionString)
    {
        var administrator = Ids.New();
        await using (var setup = fixture.Scope(SystemPrincipal.SystemId, connectionString: connectionString))
        {
            await RoleServiceTests.AssignAsync(setup.Scope, administrator, SystemRoles.TenantAdministrator);
        }

        return fixture.Scope(administrator, connectionString: connectionString);
    }

    private async Task<Role?> FindAsync(string connectionString, string code)
    {
        await using var scope = fixture.Scope(SystemPrincipal.SystemId, connectionString: connectionString);
        return await scope.Get<AuthorizationDbContext>().Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Code == code, TestContext.Current.CancellationToken);
    }
}
