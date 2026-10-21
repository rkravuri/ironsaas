using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>The tree's rules without a database (ADR-0030): paths, containment, moves, codes and tree order.</summary>
public class OrganisationTreeRuleTests
{
    private static readonly OrganisationUnit Root = OrganisationUnit.Root("Acme", "acme", KernelOrganisationKinds.Root);

    [Fact]
    [Proves("ADR-0030")]
    public void AChildsPathIsItsParentsPathAndItsOwnId()
    {
        var sales = OrganisationUnit.ChildOf(Root, "Sales", "sales", KernelOrganisationKinds.Unit);
        var north = OrganisationUnit.ChildOf(sales, "North", "north", KernelOrganisationKinds.Unit);

        Assert.Equal($"/{Root.Id:N}/", Root.Path);
        Assert.Equal($"/{Root.Id:N}/{sales.Id:N}/{north.Id:N}/", north.Path);
        Assert.Equal(2, north.Depth);
        Assert.Equal(sales.Id, north.ParentId);
        Assert.Equal([Root.Id, sales.Id, north.Id], north.AncestorsAndSelf());
    }

    [Fact]
    [Proves("ADR-0030")]
    public void AUnitContainsItselfAndItsDescendantsOnly()
    {
        var sales = OrganisationUnit.ChildOf(Root, "Sales", "sales", KernelOrganisationKinds.Unit);
        var north = OrganisationUnit.ChildOf(sales, "North", "north", KernelOrganisationKinds.Unit);
        var finance = OrganisationUnit.ChildOf(Root, "Finance", "finance", KernelOrganisationKinds.Unit);

        Assert.True(sales.Contains(sales));
        Assert.True(sales.Contains(north));
        Assert.False(north.Contains(sales));
        Assert.False(sales.Contains(finance));
        Assert.True(Root.Contains(finance));
    }

    [Fact]
    [Proves("ADR-0030")]
    public void MovingUnderADescendantIsACycleAndIsRefused()
    {
        var sales = OrganisationUnit.ChildOf(Root, "Sales", "sales", KernelOrganisationKinds.Unit);
        var north = OrganisationUnit.ChildOf(sales, "North", "north", KernelOrganisationKinds.Unit);

        Assert.Throws<InvalidOperationException>(() => sales.MoveUnder(north));
        Assert.Throws<InvalidOperationException>(() => sales.MoveUnder(sales));
    }

    [Fact]
    [Proves("ADR-0030")]
    public void MovingTakesTheNewParentsPathAndDepth()
    {
        var sales = OrganisationUnit.ChildOf(Root, "Sales", "sales", KernelOrganisationKinds.Unit);
        var north = OrganisationUnit.ChildOf(sales, "North", "north", KernelOrganisationKinds.Unit);
        var finance = OrganisationUnit.ChildOf(Root, "Finance", "finance", KernelOrganisationKinds.Unit);

        north.MoveUnder(finance);

        Assert.Equal(finance.Id, north.ParentId);
        Assert.Equal($"{finance.Path}{north.Id:N}/", north.Path);
        Assert.Equal(2, north.Depth);
    }

    [Theory]
    [Proves("TF-034 AC5")]
    [InlineData("sales", true)]
    [InlineData("sales-north-2", true)]
    [InlineData("-sales", false)]
    [InlineData("Sales", false)]
    [InlineData("sales north", false)]
    [InlineData("", false)]
    public void ACodeIsLowercaseLettersDigitsAndHyphens(string code, bool valid)
    {
        Assert.Equal(valid, OrganisationUnit.IsValidCode(code));
    }

    [Fact]
    [Proves("TF-034 AC5")]
    public void ACodeIsStoredInLowercase()
    {
        Assert.Equal("north", OrganisationUnit.ChildOf(Root, "North", " North ", KernelOrganisationKinds.Unit).Code);
    }

    [Fact]
    [Proves("ADR-0030")]
    public void NoUnitSitsDeeperThanTheLimit()
    {
        var unit = Root;
        for (var depth = 1; depth <= OrganisationUnit.MaxDepth; depth++)
        {
            unit = OrganisationUnit.ChildOf(unit, $"Level {depth}", $"level-{depth}", KernelOrganisationKinds.Unit);
        }

        Assert.True(unit.Path.Length <= OrganisationUnit.PathMaxLength);
        Assert.Throws<ArgumentOutOfRangeException>(() => OrganisationUnit.ChildOf(unit, "Too deep", "too-deep", KernelOrganisationKinds.Unit));
    }

    [Fact]
    [Proves("TF-034 AC8")]
    public void TheTreeIsListedDepthFirstWithSiblingsByName()
    {
        var sales = OrganisationUnit.ChildOf(Root, "Sales", "sales", KernelOrganisationKinds.Unit);
        var north = OrganisationUnit.ChildOf(sales, "North", "north", KernelOrganisationKinds.Unit);
        var east = OrganisationUnit.ChildOf(sales, "east", "east", KernelOrganisationKinds.Unit);
        var finance = OrganisationUnit.ChildOf(Root, "Finance", "finance", KernelOrganisationKinds.Unit);

        var ordered = OrganisationUnitService.InTreeOrder([north, finance, Root, east, sales]);

        Assert.Equal(["acme", "finance", "sales", "east", "north"], ordered.Select(u => u.Code));
    }
}

/// <summary>Which grants apply at a scope (ADR-0030), without a database.</summary>
public class PrincipalGrantsTests
{
    private static readonly Guid Root = Ids.New();
    private static readonly Guid Sales = Ids.New();
    private static readonly Guid North = Ids.New();
    private static readonly Guid Finance = Ids.New();
    private static readonly Guid Order = Ids.New();

    private static readonly IReadOnlyList<Guid> NorthPath = [Root, Sales, North];
    private static readonly IReadOnlyList<Guid> SalesPath = [Root, Sales];
    private static readonly IReadOnlyList<Guid> FinancePath = [Root, Finance];

    [Fact]
    [Proves("TF-033 AC2")]
    public void ATenantGrantAppliesEverywhere()
    {
        var grants = new PrincipalGrants([new Grant(RoleScopeKind.Tenant, null, null, ["a.b.read"])]);

        Assert.Contains("a.b.read", grants.PermissionsAt(AccessScope.Tenant, []));
        Assert.Contains("a.b.read", grants.PermissionsAt(AccessScope.Unit(North), NorthPath));
        Assert.Contains("a.b.read", grants.PermissionsAt(AccessScope.Record("order", Order, Finance), FinancePath));
    }

    [Fact]
    [Proves("TF-033 AC1", "ADR-0030")]
    public void AUnitGrantAppliesToThatUnitOnlyAndNotTenantWide()
    {
        var grants = new PrincipalGrants([new Grant(RoleScopeKind.Unit, Sales, null, ["a.b.read"])]);

        Assert.Contains("a.b.read", grants.PermissionsAt(AccessScope.Record("order", Order, Sales), SalesPath));
        Assert.DoesNotContain("a.b.read", grants.PermissionsAt(AccessScope.Record("order", Order, North), NorthPath));
        Assert.DoesNotContain("a.b.read", grants.PermissionsAt(AccessScope.Record("order", Order, Finance), FinancePath));
        Assert.DoesNotContain("a.b.read", grants.PermissionsAt(AccessScope.Tenant, []));
    }

    [Fact]
    [Proves("TF-033 AC3")]
    public void AGrantWithChildrenAppliesToItsSubtree()
    {
        var grants = new PrincipalGrants([new Grant(RoleScopeKind.UnitWithChildren, Sales, null, ["a.b.read"])]);

        Assert.Contains("a.b.read", grants.PermissionsAt(AccessScope.Unit(Sales), SalesPath));
        Assert.Contains("a.b.read", grants.PermissionsAt(AccessScope.Record("order", Order, North), NorthPath));
        Assert.DoesNotContain("a.b.read", grants.PermissionsAt(AccessScope.Unit(Finance), FinancePath));
    }

    [Fact]
    [Proves("ADR-0030")]
    public void ARecordGrantAppliesToThatRecordOfThatTypeOnly()
    {
        var grants = new PrincipalGrants([new Grant(RoleScopeKind.Record, Order, "order", ["a.b.read"])]);

        Assert.Contains("a.b.read", grants.PermissionsAt(AccessScope.Record("order", Order, Finance), FinancePath));
        Assert.DoesNotContain("a.b.read", grants.PermissionsAt(AccessScope.Record("invoice", Order, Finance), FinancePath));
        Assert.DoesNotContain("a.b.read", grants.PermissionsAt(AccessScope.Unit(Finance), FinancePath));
    }

    [Fact]
    [Proves("ADR-0031")]
    public void AnywhereHoldsEveryPermissionOfEveryGrant()
    {
        var grants = new PrincipalGrants(
        [
            new Grant(RoleScopeKind.Unit, North, null, ["a.b.read"]),
            new Grant(RoleScopeKind.Record, Order, "order", ["a.b.approve"]),
        ]);

        Assert.Equal(["a.b.approve", "a.b.read"], grants.PermissionsAt(AccessScope.Anywhere, []).Order(StringComparer.Ordinal));
        Assert.Empty(grants.TenantWide);
    }

    [Fact]
    [Proves("ADR-0031")]
    public void AGrantWithChildrenIsCoveredOnlyByGrantsThatReachTheWholeSubtree()
    {
        var grants = new PrincipalGrants(
        [
            new Grant(RoleScopeKind.Unit, Sales, null, ["a.b.manage"]),
            new Grant(RoleScopeKind.UnitWithChildren, Root, null, ["a.b.read"]),
        ]);

        Assert.Contains("a.b.manage", grants.PermissionsCovering(RoleScope.Unit(Sales), SalesPath));
        Assert.DoesNotContain("a.b.manage", grants.PermissionsCovering(RoleScope.Unit(Sales, includeChildren: true), SalesPath));
        Assert.Contains("a.b.read", grants.PermissionsCovering(RoleScope.Unit(Sales, includeChildren: true), SalesPath));
        Assert.Empty(grants.PermissionsCovering(RoleScope.Tenant, []));
    }

    [Fact]
    [Proves("TF-033 AC2")]
    public void AnUnknownUnitGetsTheTenantWideGrantsOnly()
    {
        var grants = new PrincipalGrants(
        [
            new Grant(RoleScopeKind.Tenant, null, null, ["a.b.read"]),
            new Grant(RoleScopeKind.UnitWithChildren, Sales, null, ["a.b.manage"]),
        ]);

        Assert.Equal(["a.b.read"], grants.PermissionsAt(AccessScope.Unit(Ids.New()), []));
    }
}
