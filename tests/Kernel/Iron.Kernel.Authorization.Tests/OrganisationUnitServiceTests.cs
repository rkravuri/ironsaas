using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>TF-034 and ADR-0030 against PostgreSQL: create, rename, move and deactivate, the rules that refuse, and the audit trail.</summary>
[Collection(AuthorizationCollection.Name)]
public class OrganisationUnitServiceTests(AuthorizationFixture fixture)
{
    private readonly RecordingAuditWriter _audit = new();

    [Fact]
    public async Task AUnitIsCreatedUnderItsParentWithItsPathAndAnAuditEvent()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = await RootAsync(scope);

        var sales = await CreateAsync(units, root.Id, "Sales");

        Assert.Equal(root.Id, sales.ParentId);
        Assert.Equal($"{root.Path}{sales.Id:N}/", sales.Path);
        Assert.Equal(1, sales.Depth);
        Assert.Equal(KernelOrganisationKinds.Unit, sales.Kind);
        var created = Assert.Single(_audit.Events, e => e.Action == AuthorizationAuditEvents.UnitCreated && e.RecordId == sales.Id);
        Assert.Equal(OrganisationUnitService.RecordType, created.RecordType);
        Assert.Equal(root.Id.ToString(), created.Details?["parent_id"]);
    }

    [Fact]
    public async Task AKindTheProviderDoesNotKnowOrDoesNotAllowThereIsRefused()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = await RootAsync(scope);

        var unknown = await units.CreateAsync(root.Id, "Legal", Code(), "legal_entity", TestContext.Current.CancellationToken);
        var secondRoot = await units.CreateAsync(root.Id, "Another root", Code(), KernelOrganisationKinds.Root, TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationErrors.UnitKindUnknown, unknown.Error.Code);
        Assert.Equal(AuthorizationErrors.UnitKindNotAllowed, secondRoot.Error.Code);
        Assert.Equal("kind", Assert.Single(secondRoot.Error.Fields).Field);
    }

    [Fact]
    public async Task ACodeIsUniqueInTheTenant()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = await RootAsync(scope);
        var code = Code();
        await units.CreateAsync(root.Id, "First", code, KernelOrganisationKinds.Unit, TestContext.Current.CancellationToken);

        var second = await units.CreateAsync(root.Id, "Second", code.ToUpperInvariant(), KernelOrganisationKinds.Unit, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Conflict, second.Error.Kind);
        Assert.Equal(AuthorizationErrors.UnitCodeTaken, second.Error.Code);
        Assert.Equal("code", Assert.Single(second.Error.Fields).Field);
    }

    [Fact]
    public async Task AMoveCarriesTheSubtreeAndKeepsEveryPathInStepWithItsParents()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = await RootAsync(scope);
        var sales = await CreateAsync(units, root.Id, "Sales");
        var north = await CreateAsync(units, sales.Id, "North");
        var city = await CreateAsync(units, north.Id, "City");
        var finance = await CreateAsync(units, root.Id, "Finance");

        var moved = await units.MoveAsync(north.Id, finance.Id, TestContext.Current.CancellationToken);

        Assert.True(moved.IsSuccess);
        var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var all = await context.OrganisationUnits.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        var byId = all.ToDictionary(u => u.Id);
        Assert.Equal(finance.Id, byId[north.Id].ParentId);
        Assert.Equal($"{finance.Path}{north.Id:N}/{city.Id:N}/", byId[city.Id].Path);
        Assert.Equal(3, byId[city.Id].Depth);
        Assert.Equal(2, byId[city.Id].Version);
        Assert.All(all, unit => Assert.Equal(PathFromParents(unit, byId), unit.Path));
        var movedEvent = Assert.Single(_audit.Events, e => e.Action == AuthorizationAuditEvents.UnitMoved);
        Assert.Equal(sales.Id.ToString(), movedEvent.Details?["from_parent_id"]);
        Assert.Equal(finance.Id.ToString(), movedEvent.Details?["to_parent_id"]);
    }

    [Fact]
    public async Task AMoveUnderItselfOrADescendantIsACycleAndChangesNothing()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = await RootAsync(scope);
        var sales = await CreateAsync(units, root.Id, "Sales");
        var north = await CreateAsync(units, sales.Id, "North");

        var underDescendant = await units.MoveAsync(sales.Id, north.Id, TestContext.Current.CancellationToken);
        var underItself = await units.MoveAsync(sales.Id, sales.Id, TestContext.Current.CancellationToken);
        var theRoot = await units.MoveAsync(root.Id, sales.Id, TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationErrors.UnitCycle, underDescendant.Error.Code);
        Assert.Equal("parent_id", Assert.Single(underDescendant.Error.Fields).Field);
        Assert.Equal(AuthorizationErrors.UnitCycle, underItself.Error.Code);
        Assert.Equal(AuthorizationErrors.UnitIsRoot, theRoot.Error.Code);
        Assert.DoesNotContain(_audit.Events, e => e.Action == AuthorizationAuditEvents.UnitMoved);
        var after = await scope.ServiceProvider.GetRequiredService<OrganisationUnitService>().FindEntityAsync(sales.Id, TestContext.Current.CancellationToken);
        Assert.Equal(root.Id, after?.ParentId);
    }

    [Fact]
    public async Task AUnitWithActiveChildrenIsNotDeactivatedAndALeafIs()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = await RootAsync(scope);
        var sales = await CreateAsync(units, root.Id, "Sales");
        var north = await CreateAsync(units, sales.Id, "North");

        var parent = await units.DeactivateAsync(sales.Id, TestContext.Current.CancellationToken);
        var leaf = await units.DeactivateAsync(north.Id, TestContext.Current.CancellationToken);
        var rootUnit = await units.DeactivateAsync(root.Id, TestContext.Current.CancellationToken);
        var under = await units.CreateAsync(north.Id, "Under", Code(), KernelOrganisationKinds.Unit, TestContext.Current.CancellationToken);
        var moveUnder = await units.MoveAsync((await CreateAsync(units, root.Id, "Loose")).Id, north.Id, TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationErrors.UnitHasActiveChildren, parent.Error.Code);
        Assert.Equal(ErrorKind.Conflict, parent.Error.Kind);
        Assert.Equal(UnitStatus.Inactive, leaf.Value.Status);
        Assert.Equal(AuthorizationErrors.UnitIsRoot, rootUnit.Error.Code);
        Assert.Equal(AuthorizationErrors.UnitInactive, under.Error.Code);
        Assert.Equal(AuthorizationErrors.UnitInactive, moveUnder.Error.Code);
        Assert.Single(_audit.Events, e => e.Action == AuthorizationAuditEvents.UnitDeactivated);
        Assert.True((await units.DeactivateAsync(sales.Id, TestContext.Current.CancellationToken)).IsSuccess, "the only child is inactive now");
    }

    [Fact]
    public async Task ARenameIsAuditedWithTheOldAndTheNewName()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = await RootAsync(scope);
        var sales = await CreateAsync(units, root.Id, "Sales");

        var renamed = await units.RenameAsync(sales.Id, "  Sales and Marketing ", TestContext.Current.CancellationToken);

        Assert.Equal("Sales and Marketing", renamed.Value.Name);
        var renamedEvent = Assert.Single(_audit.Events, e => e.Action == AuthorizationAuditEvents.UnitRenamed);
        Assert.Equal("Sales", renamedEvent.Details?["from"]);
        Assert.Equal("Sales and Marketing", renamedEvent.Details?["to"]);
    }

    [Fact]
    public async Task TheTreeIsReadDepthFirstFromTheRoot()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = await RootAsync(scope);
        var parent = await CreateAsync(units, root.Id, "Tree order");
        var b = await CreateAsync(units, parent.Id, "B");
        var a = await CreateAsync(units, parent.Id, "A");
        var a1 = await CreateAsync(units, a.Id, "A1");

        var tree = await units.TreeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(root.Id, tree[0].Id);
        var mine = tree.Where(u => u.Path.StartsWith(parent.Path, StringComparison.Ordinal)).Select(u => u.Id).ToList();
        Assert.Equal([parent.Id, a.Id, a1.Id, b.Id], mine);
    }

    internal static string Code() => $"u-{Ids.New():N}";

    internal static async Task<OrganisationUnit> CreateAsync(OrganisationUnitService units, Guid parentId, string name)
    {
        var result = await units.CreateAsync(parentId, name, Code(), KernelOrganisationKinds.Unit, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        return result.Value;
    }

    internal static async Task<OrganisationUnit> RootAsync(AsyncServiceScope scope) =>
        await scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>().OrganisationUnits.AsNoTracking().SingleAsync(u => u.ParentId == null, TestContext.Current.CancellationToken);

    private static string PathFromParents(OrganisationUnit unit, IReadOnlyDictionary<Guid, OrganisationUnit> byId)
    {
        var segments = new List<string>();
        for (OrganisationUnit? current = unit; current is not null; current = current.ParentId is { } parent ? byId[parent] : null)
        {
            segments.Insert(0, OrganisationUnit.Segment(current.Id));
        }

        return "/" + string.Join('/', segments) + "/";
    }

    private ServiceProvider Services() => fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), _audit);
}
