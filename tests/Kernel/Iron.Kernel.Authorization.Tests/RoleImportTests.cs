using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>TF-032: roles export from one tenant's database and import into another's, with a dry run that shows the diff first.</summary>
[Collection(AuthorizationCollection.Name)]
public class RoleImportTests(AuthorizationFixture fixture)
{
    [Fact]
    public async Task RolesRoundTripFromOneTenantToAnotherWithADryRunFirst()
    {
        var bravo = await fixture.CreateTenantDatabaseAsync($"bravo_{Ids.New():N}");
        var source = await AdministratorAsync(fixture.ConnectionString);
        var target = await AdministratorAsync(bravo);
        var auditor = Code("auditor");
        var planner = Code("planner");
        Assert.True((await source.Roles.CreateAsync(auditor, new RoleInput("Auditor", "Reads roles.", PrincipalType.User, [AuthorizationPermissions.RolesRead.Name]), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await source.Roles.CreateAsync(planner, new RoleInput("Planner", "Shapes the tree.", PrincipalType.User, [AuthorizationPermissions.UnitsRead.Name, AuthorizationPermissions.UnitsManage.Name]), TestContext.Current.CancellationToken)).IsSuccess);
        var export = Only(await source.Roles.ExportAsync(TestContext.Current.CancellationToken), auditor, planner);

        var dryRun = await target.Roles.ImportAsync(export, dryRun: true, TestContext.Current.CancellationToken);
        Assert.False(dryRun.Value.Applied);
        Assert.Equal([auditor, planner], dryRun.Value.Created);
        Assert.Null(await FindAsync(bravo, auditor));

        var applied = await target.Roles.ImportAsync(export, dryRun: false, TestContext.Current.CancellationToken);
        Assert.True(applied.Value.Applied);
        Assert.Equal(["authz.units.manage", "authz.units.read"], (await FindAsync(bravo, planner))?.Permissions);
        Assert.Equal(2, target.Audit.Events.Count(e => e.Action == AuthorizationAuditEvents.RoleCreated && e.Reason == "import"));

        var again = await target.Roles.ImportAsync(export, dryRun: true, TestContext.Current.CancellationToken);
        Assert.Equal([auditor, planner], again.Value.Unchanged);
        Assert.Empty(again.Value.Created);

        var planned = export.Roles.Single(r => r.Code == planner);
        var changedExport = export with { Roles = [export.Roles.Single(r => r.Code == auditor), planned with { Permissions = [AuthorizationPermissions.UnitsRead.Name, AuthorizationPermissions.RolesRead.Name] }] };
        var change = Assert.Single((await target.Roles.ImportAsync(changedExport, dryRun: true, TestContext.Current.CancellationToken)).Value.Changed);
        Assert.Equal(planner, change.Code);
        Assert.Equal([AuthorizationPermissions.RolesRead.Name], change.Added);
        Assert.Equal([AuthorizationPermissions.UnitsManage.Name], change.Removed);
    }

    [Fact]
    public async Task UnknownPermissionsAreShownInTheDryRunAndRefuseTheApplyAndSystemRolesAreNeverImported()
    {
        var target = await AdministratorAsync(fixture.ConnectionString);
        var buyer = Code("buyer");
        var document = new RoleExport(RoleExport.CurrentFormat,
        [
            new RoleDocument(buyer, "Buyer", string.Empty, PrincipalType.User, [AuthorizationPermissions.RolesRead.Name, "procurement.orders.create"]),
            new RoleDocument(SystemRoles.TenantAdministrator, "Mine", string.Empty, PrincipalType.User, []),
        ]);

        var dryRun = await target.Roles.ImportAsync(document, dryRun: true, TestContext.Current.CancellationToken);
        var apply = await target.Roles.ImportAsync(document, dryRun: false, TestContext.Current.CancellationToken);

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
        await using (var setup = Services(fixture.ConnectionString, SystemPrincipal.SystemId).CreateAsyncScope())
        {
            var context = setup.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
            context.Roles.Add(new Role(managerRole, "Role manager", string.Empty, isSystem: false, PrincipalType.User, [AuthorizationPermissions.RolesRead.Name, AuthorizationPermissions.RolesManage.Name]));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await RoleServiceTests.AssignAsync(setup, importer, managerRole);
        }

        await using var scope = Services(fixture.ConnectionString, importer).CreateAsyncScope();
        var document = new RoleExport(RoleExport.CurrentFormat, [new RoleDocument(Code("granter"), "Granter", string.Empty, PrincipalType.User, [AuthorizationPermissions.AssignmentsManage.Name])]);

        var apply = await scope.ServiceProvider.GetRequiredService<RoleService>().ImportAsync(document, dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationErrors.RoleEscalation, apply.Error.Code);
    }

    private static string Code(string name) => $"{name}-{Ids.New():N}"[..30];

    private static RoleExport Only(RoleExport export, params string[] codes) => export with { Roles = export.Roles.Where(r => codes.Contains(r.Code)).ToList() };

    private async Task<(RecordingAuditWriter Audit, RoleService Roles)> AdministratorAsync(string connectionString)
    {
        var administrator = Ids.New();
        await using (var setup = Services(connectionString, SystemPrincipal.SystemId).CreateAsyncScope())
        {
            await RoleServiceTests.AssignAsync(setup, administrator, SystemRoles.TenantAdministrator);
        }

        var audit = new RecordingAuditWriter();
        var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(administrator), audit, connectionString);
        return (audit, provider.CreateScope().ServiceProvider.GetRequiredService<RoleService>());
    }

    private async Task<Role?> FindAsync(string connectionString, string code)
    {
        await using var scope = Services(connectionString, SystemPrincipal.SystemId).CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        return context.Roles.SingleOrDefault(r => r.Code == code);
    }

    private ServiceProvider Services(string connectionString, Guid principal) =>
        fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(principal), new RecordingAuditWriter(), connectionString);
}
