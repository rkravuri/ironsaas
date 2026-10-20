using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data.Policies;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>
/// The working unit against the real tree (TF-222, ADR-0038): the units a person may choose, the consolidated view, and
/// lists that keep to the working unit and the units below it while reads by id do not.
/// </summary>
[Collection(AuthorizationCollection.Name)]
public class WorkingUnitTests(AuthorizationFixture fixture)
{
    [Fact]
    public async Task APersonMayWorkForTheirHomeUnitAndTheActiveUnitsOfTheirOwnRoles()
    {
        await using var admin = fixture.Scope(SystemPrincipal.SystemId);
        var (root, sales, north, finance) = await TreeAsync(admin);
        var units = admin.Get<OrganisationUnitService>();
        var closed = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Closed");
        var person = fixture.Principals.Add(homeUnit: finance.Id);
        await RoleServiceTests.AssignAsync(admin.Scope, person, SystemRoles.ReadOnly, RoleScope.Unit(north.Id));
        await RoleServiceTests.AssignAsync(admin.Scope, person, SystemRoles.ReadOnly, RoleScope.Unit(closed.Id));
        Assert.True((await units.DeactivateAsync(closed.Id, TestContext.Current.CancellationToken)).IsSuccess);
        var someoneElse = fixture.Principals.Add(homeUnit: root.Id);
        await RoleServiceTests.AssignAsync(admin.Scope, someoneElse, SystemRoles.ReadOnly, RoleScope.Unit(sales.Id));

        var options = await admin.Get<IWorkingUnits>().OptionsAsync(person, finance.Id, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { north.Id, finance.Id }.Order(), options.Units.Select(u => u.Id).Order());
        Assert.Equal([root.Name, "Sales", "North"], options.Units.Single(u => u.Id == north.Id).Path.Select(step => step.Name));
        Assert.Equal(2, options.Units.Single(u => u.Id == north.Id).Depth);
        Assert.False(options.AllUnitsAvailable, "two units on their own: one at a time, no consolidated view");
    }

    [Fact]
    public async Task AllMyUnitsIsOfferedWhereARoleReachesAParentWithChildrenOrTheWholeTenant()
    {
        await using var admin = fixture.Scope(SystemPrincipal.SystemId);
        var (root, sales, north, finance) = await TreeAsync(admin);
        var parentWithChildren = fixture.Principals.Add(homeUnit: finance.Id);
        await RoleServiceTests.AssignAsync(admin.Scope, parentWithChildren, SystemRoles.ReadOnly, RoleScope.Unit(sales.Id, includeChildren: true));
        var leafWithChildren = fixture.Principals.Add(homeUnit: finance.Id);
        await RoleServiceTests.AssignAsync(admin.Scope, leafWithChildren, SystemRoles.ReadOnly, RoleScope.Unit(north.Id, includeChildren: true));
        var tenantWide = fixture.Principals.Add(homeUnit: finance.Id);
        await RoleServiceTests.AssignAsync(admin.Scope, tenantWide, SystemRoles.ReadOnly, RoleScope.Tenant);
        var workingUnits = admin.Get<IWorkingUnits>();

        var parent = await workingUnits.OptionsAsync(parentWithChildren, finance.Id, TestContext.Current.CancellationToken);
        var leaf = await workingUnits.OptionsAsync(leafWithChildren, finance.Id, TestContext.Current.CancellationToken);
        var tenant = await workingUnits.OptionsAsync(tenantWide, finance.Id, TestContext.Current.CancellationToken);
        var homeOnly = await workingUnits.OptionsAsync(fixture.Principals.Add(homeUnit: finance.Id), finance.Id, TestContext.Current.CancellationToken);

        Assert.True(parent.AllUnitsAvailable, "TF-222 AC1: a parent with its children");
        Assert.Equal(new[] { sales.Id, finance.Id }.Order(), parent.Units.Select(u => u.Id).Order());
        Assert.False(leaf.AllUnitsAvailable, "with children, but there are none below it");
        Assert.True(tenant.AllUnitsAvailable);
        Assert.Contains(tenant.Units, u => u.Id == root.Id);
        Assert.Equal([finance.Id], homeOnly.Units.Select(u => u.Id));
        Assert.False(homeOnly.AllUnitsAvailable, "one unit: the header shows no control");
    }

    [Fact]
    public async Task ListsKeepToTheWorkingUnitAndItsChildrenAndTheConsolidatedViewShowsEverythingTheRolesReach()
    {
        Guid person;
        string batch = $"batch {Ids.New():N}";
        (OrganisationUnit Root, OrganisationUnit Sales, OrganisationUnit North, OrganisationUnit Finance) tree;
        Guid financeCrate;
        await using (var admin = fixture.Scope(SystemPrincipal.SystemId))
        {
            tree = await TreeAsync(admin);
            var role = await CrateReaderAsync(admin);
            person = fixture.Principals.Add(homeUnit: tree.Finance.Id);
            await RoleServiceTests.AssignAsync(admin.Scope, person, role, RoleScope.Unit(tree.Sales.Id, includeChildren: true));
            await RoleServiceTests.AssignAsync(admin.Scope, person, role, RoleScope.Unit(tree.Finance.Id));
            var probe = admin.Get<ProbeDbContext>();
            var crates = new[] { new Crate(tree.Sales.Id, $"{batch} sales"), new Crate(tree.North.Id, $"{batch} north"), new Crate(tree.Finance.Id, $"{batch} finance") };
            probe.Crates.AddRange(crates);
            await probe.SaveChangesAsync(TestContext.Current.CancellationToken);
            financeCrate = crates[2].Id;
        }

        var atSales = await LabelsAsync(fixture.WorkingScope(person, tree.Sales.Id), batch);
        var atFinance = await LabelsAsync(fixture.WorkingScope(person, tree.Finance.Id), batch);
        var everywhere = await LabelsAsync(fixture.WorkingScope(person, tree.Finance.Id, allUnits: true), batch);

        Assert.Equal(2, atSales.Total);
        Assert.Equal([$"{batch} north", $"{batch} sales"], atSales.Labels);
        Assert.Equal([$"{batch} finance"], atFinance.Labels);
        Assert.Equal(3, everywhere.Total);

        // A read by id is not narrowed: a link to a record of another of the person's units still opens.
        await using var working = fixture.WorkingScope(person, tree.Sales.Id);
        var opened = await working.Get<IRecordPolicies>().FindAsync<Crate>(financeCrate, RecordActions.Read, TestContext.Current.CancellationToken);
        Assert.True(opened.IsSuccess);
    }

    [Fact]
    public async Task WhileActingForSomeoneListsAreNotNarrowedByTheWorkingUnit()
    {
        await using var admin = fixture.Scope(SystemPrincipal.SystemId);
        var (_, sales, _, finance) = await TreeAsync(admin);
        var person = fixture.Principals.Add(homeUnit: finance.Id);
        var reach = new UnitReach(admin.Get<AuthorizationDbContext>(), fixture.Principals, new FixedPrincipal(person, actingFor: Ids.New(), workingUnit: sales.Id));
        var forAnother = new UnitReach(admin.Get<AuthorizationDbContext>(), fixture.Principals, new FixedPrincipal(Ids.New(), workingUnit: sales.Id));
        var own = new UnitReach(admin.Get<AuthorizationDbContext>(), fixture.Principals, new FixedPrincipal(person, workingUnit: sales.Id));

        Assert.Null(await reach.WorkingUnitsAsync(person, TestContext.Current.CancellationToken));
        Assert.Null(await forAnother.WorkingUnitsAsync(person, TestContext.Current.CancellationToken));
        Assert.Contains(sales.Id, (await own.WorkingUnitsAsync(person, TestContext.Current.CancellationToken))!);
    }

    private static async Task<(long Total, string[] Labels)> LabelsAsync(TestScope scope, string batch)
    {
        await using (scope)
        {
            var probe = scope.Get<ProbeDbContext>();
            var policed = await scope.Get<IRecordPolicies>().ApplyAsync(probe.Crates.Where(c => c.Label.StartsWith(batch)).OrderBy(c => c.Label), TestContext.Current.CancellationToken);
            var page = await policed.ToPageAsync(new PageRequest(1, 10), TestContext.Current.CancellationToken);
            return (page.TotalCount, [.. page.Items.Select(c => c.Label)]);
        }
    }

    private static async Task<string> CrateReaderAsync(TestScope admin)
    {
        var role = new Role(RoleServiceTests.Code(), "Crate reader", string.Empty, isSystem: false, PrincipalKind.User, [ProbeRecords.CratesRead.Name]);
        var context = admin.Get<AuthorizationDbContext>();
        context.Roles.Add(role);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return role.Code;
    }

    /// <summary>A small tree of its own under the root: Sales with North under it, and Finance.</summary>
    private static async Task<(OrganisationUnit Root, OrganisationUnit Sales, OrganisationUnit North, OrganisationUnit Finance)> TreeAsync(TestScope scope)
    {
        var units = scope.Get<OrganisationUnitService>();
        var root = await OrganisationUnitServiceTests.RootAsync(scope.Scope);
        var sales = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Sales");
        var north = await OrganisationUnitServiceTests.CreateAsync(units, sales.Id, "North");
        var finance = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Finance");
        return (root, sales, north, finance);
    }
}
