using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>The pure rule of segregation of duties (TF-036, ADR-0039): when two grants meet at a unit, and which side a grant holds.</summary>
public class ConflictDetectorTests
{
    private static readonly Guid Root = Guid.Parse("01a19000-0000-7000-8000-000000000001");
    private static readonly Guid Sales = Guid.Parse("01a19000-0000-7000-8000-000000000002");
    private static readonly Guid North = Guid.Parse("01a19000-0000-7000-8000-000000000003");
    private static readonly Guid Finance = Guid.Parse("01a19000-0000-7000-8000-000000000004");
    private static readonly Guid Creator = Guid.Parse("01a19000-0000-7000-8000-0000000000c1");
    private static readonly Guid Approver = Guid.Parse("01a19000-0000-7000-8000-0000000000a1");

    private static readonly ConflictRuleView Rule = new(Ids.New(), "Create and approve", ConflictModes.Block, "Four eyes.", new ConflictSide(ConflictSides.Permission, "p.suppliers.create"), new ConflictSide(ConflictSides.Permission, "p.suppliers.approve"));

    private static readonly Dictionary<Guid, IReadOnlyList<Guid>> Paths = new()
    {
        [Root] = [Root],
        [Sales] = [Root, Sales],
        [North] = [Root, Sales, North],
        [Finance] = [Root, Finance],
    };

    [Fact]
    [Proves("ADR-0039")]
    public void GrantsAtUnrelatedUnitsAreNoConflictAndAParentWithChildrenMeetsTheUnitBelow()
    {
        var unrelated = ConflictDetector.Find([Rule], [Unit("p.suppliers.create", Sales), Unit("p.suppliers.approve", Finance)], onlyNew: false);
        var alone = ConflictDetector.Find([Rule], [Unit("p.suppliers.create", Sales), Unit("p.suppliers.approve", North)], onlyNew: false);
        var below = ConflictDetector.Find([Rule], [Unit("p.suppliers.create", Sales, withChildren: true), Unit("p.suppliers.approve", North)], onlyNew: false);
        var tenant = ConflictDetector.Find([Rule], [Tenant("p.suppliers.create"), Unit("p.suppliers.approve", Finance)], onlyNew: false);

        Assert.Empty(unrelated);
        Assert.Empty(alone);
        Assert.Equal(North, Assert.Single(below).UnitId);
        Assert.Equal(Finance, Assert.Single(tenant).UnitId);
    }

    [Fact]
    [Proves("ADR-0039")]
    public void OneGrantWhoseRoleHoldsBothSidesIsAConflictAtItsUnit()
    {
        var both = ConflictDetector.Find([Rule], [Unit("p.suppliers.create", Sales, also: "p.suppliers.approve")], onlyNew: false);

        Assert.Equal(Sales, Assert.Single(both).UnitId);
    }

    [Fact]
    [Proves("ADR-0039")]
    public void ARecordGrantMeetsATenantGrantAndTheSameRecordOnly()
    {
        var record = Ids.New();
        var withTenant = ConflictDetector.Find([Rule], [Record("p.suppliers.create", record), Tenant("p.suppliers.approve")], onlyNew: false);
        var withUnit = ConflictDetector.Find([Rule], [Record("p.suppliers.create", record), Unit("p.suppliers.approve", Sales, withChildren: true)], onlyNew: false);
        var sameRecord = ConflictDetector.Find([Rule], [Record("p.suppliers.create", record), Record("p.suppliers.approve", record)], onlyNew: false);

        Assert.Single(withTenant);
        Assert.Empty(withUnit);
        Assert.Single(sameRecord);
    }

    [Fact]
    [Proves("ADR-0039")]
    public void ARoleSideIsHeldOnlyWithEveryPermissionOfTheRole()
    {
        var roles = new ConflictRuleView(Ids.New(), "Roles", ConflictModes.Warn, string.Empty, new ConflictSide(ConflictSides.Role, Creator.ToString("D")), new ConflictSide(ConflictSides.Role, Approver.ToString("D")));
        var whole = Unit("p.a", Sales) with { RoleId = Creator, RolePermissions = ["p.a"] };
        var approving = Unit("p.b", Sales) with { RoleId = Approver, RolePermissions = ["p.b", "p.c"] };
        var part = approving with { Permissions = ["p.b"], DelegationId = Ids.New() };

        Assert.Single(ConflictDetector.Find([roles], [whole, approving with { Permissions = ["p.b", "p.c"] }], onlyNew: false));
        Assert.Empty(ConflictDetector.Find([roles], [whole, part], onlyNew: false));
    }

    [Fact]
    [Proves("ADR-0039")]
    public void OnlyTheConflictsANewGrantTakesPartInAreItsDoing()
    {
        var old = new[] { Unit("p.suppliers.create", Sales), Unit("p.suppliers.approve", Sales) };
        var created = Unit("p.suppliers.approve", Sales, withChildren: true) with { IsNew = true, AssignmentId = null };

        Assert.Empty(ConflictDetector.Find([Rule], old, onlyNew: true));
        Assert.Single(ConflictDetector.Find([Rule], [.. old, created], onlyNew: true));
        Assert.Equal(2, ConflictDetector.Find([Rule], [.. old, created], onlyNew: false).Count);
    }

    private static HeldGrant Unit(string permission, Guid unit, bool withChildren = false, string? also = null) =>
        new(Ids.New(), null, Ids.New(), "A role", also is null ? [permission] : [permission, also], also is null ? [permission] : [permission, also], withChildren ? RoleScopeKind.UnitWithChildren : RoleScopeKind.Unit, unit, null, Paths[unit], IsNew: false);

    private static HeldGrant Tenant(string permission) =>
        new(Ids.New(), null, Ids.New(), "A role", [permission], [permission], RoleScopeKind.Tenant, null, null, [], IsNew: false);

    private static HeldGrant Record(string permission, Guid record) =>
        new(Ids.New(), null, Ids.New(), "A role", [permission], [permission], RoleScopeKind.Record, record, "supplier", [], IsNew: false);
}
