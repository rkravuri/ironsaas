namespace Iron.Kernel.Tests;

public class AccessScopeTests
{
    private static readonly Guid Unit = Guid.Parse("01a15000-0000-7000-8000-000000000001");
    private static readonly Guid Record = Guid.Parse("01a15000-0000-7000-8000-000000000002");

    [Fact]
    public void TheTenantScopeHasNoUnitAndNoRecord()
    {
        Assert.Equal(AccessScopeKind.Tenant, AccessScope.Tenant.Kind);
        Assert.Null(AccessScope.Tenant.UnitId);
        Assert.Null(AccessScope.Tenant.RecordId);
        Assert.Equal("tenant", AccessScope.Tenant.ToString());
    }

    [Fact]
    public void ARecordScopeCarriesTheUnitThatOwnsIt()
    {
        var scope = AccessScope.Record("purchase_order", Record, Unit);

        Assert.Equal(AccessScopeKind.Record, scope.Kind);
        Assert.Equal(Unit, scope.UnitId);
        Assert.Equal("purchase_order", scope.RecordType);
        Assert.Equal(Record, scope.RecordId);
        Assert.Equal($"record:purchase_order:{Record:N}", scope.ToString());
    }

    [Fact]
    public void ScopesAreValues()
    {
        Assert.Equal(AccessScope.Unit(Unit), AccessScope.Unit(Unit));
        Assert.NotEqual(AccessScope.Unit(Unit), AccessScope.Unit(Record));
        Assert.Equal($"unit:{Unit:N}", AccessScope.Unit(Unit).ToString());
    }

    [Fact]
    public void AnywhereAndARecordWithoutItsUnitAreScopesToo()
    {
        Assert.Equal("anywhere", AccessScope.Anywhere.ToString());
        Assert.Equal(AccessScopeKind.Anywhere, AccessScope.Anywhere.Kind);
        var record = AccessScope.Record("order", Record, owningUnitId: null);
        Assert.Null(record.UnitId);
        Assert.Equal(AccessScopeKind.Record, record.Kind);
    }

    [Fact]
    public void EmptyIdsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => AccessScope.Unit(Guid.Empty));
        Assert.Throws<ArgumentException>(() => AccessScope.Record("thing", Guid.Empty, Unit));
        Assert.Throws<ArgumentException>(() => AccessScope.Record("thing", Record, Guid.Empty));
        Assert.Throws<ArgumentException>(() => AccessScope.Record(" ", Record, Unit));
    }
}

public class RecordAccessTests
{
    private static readonly Guid UnitA = Guid.Parse("01a15000-0000-7000-8000-00000000000a");
    private static readonly Guid UnitB = Guid.Parse("01a15000-0000-7000-8000-00000000000b");
    private static readonly Guid Granted = Guid.Parse("01a15000-0000-7000-8000-0000000000c1");
    private static readonly Guid Other = Guid.Parse("01a15000-0000-7000-8000-0000000000c2");

    [Fact]
    public void EverythingAllowsAnyRecord()
    {
        Assert.True(RecordAccess.Everything.Allows(UnitB, Other));
        Assert.False(RecordAccess.Everything.IsNothing);
    }

    [Fact]
    public void WithinAllowsTheUnitsRecordsAndTheRecordsGrantedOneByOne()
    {
        var access = RecordAccess.Within([UnitA, UnitA], [Granted]);

        Assert.Equal([UnitA], access.Units);
        Assert.True(access.Allows(UnitA, Other));
        Assert.True(access.Allows(UnitB, Granted));
        Assert.False(access.Allows(UnitB, Other));
    }

    [Fact]
    public void NothingAllowsNothing()
    {
        Assert.True(RecordAccess.Nothing.IsNothing);
        Assert.False(RecordAccess.Nothing.Allows(UnitA, Granted));
    }
}
