using System.Diagnostics;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>TF-033 and ADR-0030 against PostgreSQL: a role scoped to a unit resolves for that unit's records and not for others.</summary>
[Collection(AuthorizationCollection.Name)]
public class ScopeEvaluationTests(AuthorizationFixture fixture)
{
    private static readonly Permission ReadUnits = AuthorizationPermissions.UnitsRead;

    [Fact]
    public async Task ARoleScopedToAUnitResolvesForThatUnitsRecordsAndNotForOthers()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var (_, sales, north, finance) = await TreeAsync(scope);
        var alice = Ids.New();
        await AssignAsync(scope, alice, RoleScope.Unit(sales.Id));
        var evaluator = scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>();

        Assert.True(await AllowedAsync(evaluator, alice, AccessScope.Record("order", Ids.New(), sales.Id)));
        Assert.False(await AllowedAsync(evaluator, alice, AccessScope.Record("order", Ids.New(), finance.Id)), "TF-033 AC1: another unit's record");
        Assert.False(await AllowedAsync(evaluator, alice, AccessScope.Record("order", Ids.New(), north.Id)), "without children, a child unit's record");
        Assert.False(await AllowedAsync(evaluator, alice, AccessScope.Tenant), "a unit grant is not tenant-wide");
    }

    [Fact]
    public async Task ARoleWithChildrenResolvesForTheSubtree()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var (_, sales, north, finance) = await TreeAsync(scope);
        var bob = Ids.New();
        await AssignAsync(scope, bob, RoleScope.Unit(sales.Id, includeChildren: true));
        var assignments = scope.ServiceProvider.GetRequiredService<IRoleAssignments>();

        Assert.Contains(ReadUnits.Name, await assignments.PermissionsOfAsync(bob, AccessScope.Record("order", Ids.New(), north.Id), TestContext.Current.CancellationToken));
        Assert.Empty(await assignments.PermissionsOfAsync(bob, AccessScope.Unit(finance.Id), TestContext.Current.CancellationToken));
        Assert.Empty(await assignments.PermissionsOfAsync(bob, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAssignmentFollowsItsUnitWhenTheUnitMoves()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var (_, sales, north, finance) = await TreeAsync(scope);
        var carol = Ids.New();
        await AssignAsync(scope, carol, RoleScope.Unit(finance.Id, includeChildren: true));
        var evaluator = scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>();
        Assert.False(await AllowedAsync(evaluator, carol, AccessScope.Unit(north.Id)));

        var moved = await scope.ServiceProvider.GetRequiredService<OrganisationUnitService>().MoveAsync(north.Id, finance.Id, TestContext.Current.CancellationToken);

        Assert.True(moved.IsSuccess);
        Assert.True(await AllowedAsync(evaluator, carol, AccessScope.Unit(north.Id)), "TF-034 AC1: the subtree came with the move");
        Assert.False(await AllowedAsync(evaluator, carol, AccessScope.Unit(sales.Id)));
    }

    [Fact]
    public async Task ARecordScopedRoleResolvesForThatRecordOnly()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var (_, _, _, finance) = await TreeAsync(scope);
        var dave = Ids.New();
        var order = Ids.New();
        await AssignAsync(scope, dave, RoleScope.Record("order", order));
        var evaluator = scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>();

        Assert.True(await AllowedAsync(evaluator, dave, AccessScope.Record("order", order, finance.Id)));
        Assert.False(await AllowedAsync(evaluator, dave, AccessScope.Record("order", Ids.New(), finance.Id)));
    }

    [Fact]
    public async Task AnAssignmentToAUnitThatDoesNotExistOrIsInactiveIsRefused()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var (_, _, north, _) = await TreeAsync(scope);
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        Assert.True((await units.DeactivateAsync(north.Id, TestContext.Current.CancellationToken)).IsSuccess);
        var assignments = scope.ServiceProvider.GetRequiredService<IRoleAssignments>();

        var missing = await assignments.AssignAsync(Ids.New(), SystemRoles.ReadOnly, RoleScope.Unit(Ids.New()), TestContext.Current.CancellationToken);
        var inactive = await assignments.AssignAsync(Ids.New(), SystemRoles.ReadOnly, RoleScope.Unit(north.Id), TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationErrors.UnitNotFound, missing.Error.Code);
        Assert.Equal(AuthorizationErrors.UnitInactive, inactive.Error.Code);
    }

    [Fact]
    public async Task AScopedCheckOverAListIsOneQueryWhateverTheNumberOfRows()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var (_, sales, north, finance) = await TreeAsync(scope);
        var erin = Ids.New();
        await AssignAsync(scope, erin, RoleScope.Unit(sales.Id, includeChildren: true));
        await AssignAsync(scope, erin, RoleScope.Record("order", Ids.New()));
        var access = scope.ServiceProvider.GetRequiredService<IRecordAccess>();
        await access.AccessibleAsync(erin, ReadUnits, "order", TestContext.Current.CancellationToken); // warms the principal's cache

        var (reached, queries) = await CountingQueriesAsync(() => access.AccessibleAsync(erin, ReadUnits, "order", TestContext.Current.CancellationToken));

        Assert.Equal(1, queries);
        Assert.Contains(sales.Id, reached.Units);
        Assert.Contains(north.Id, reached.Units);
        Assert.DoesNotContain(finance.Id, reached.Units);
        Assert.Single(reached.Records);
        Assert.True(reached.Allows(north.Id, Ids.New()));
        Assert.False(reached.Allows(finance.Id, Ids.New()));
    }

    [Fact]
    public async Task ATenantWideGrantReachesEverythingWithoutAQuery()
    {
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var frank = Ids.New();
        await AssignAsync(scope, frank, RoleScope.Tenant);
        var access = scope.ServiceProvider.GetRequiredService<IRecordAccess>();
        await access.AccessibleAsync(frank, ReadUnits, "order", TestContext.Current.CancellationToken);

        var (reached, queries) = await CountingQueriesAsync(() => access.AccessibleAsync(frank, ReadUnits, "order", TestContext.Current.CancellationToken));

        Assert.True(reached.IsEverything);
        Assert.Equal(0, queries);
    }

    /// <summary>Counts the database commands a call runs, by the Npgsql activities in its own trace.</summary>
    private static async Task<(T Result, int Queries)> CountingQueriesAsync<T>(Func<Task<T>> call)
    {
        using var trace = new Activity("query-count").SetIdFormat(ActivityIdFormat.W3C).Start();
        var traceId = trace.TraceId;
        var count = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.TraceId == traceId)
                {
                    Interlocked.Increment(ref count);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var result = await call();
        return (result, count);
    }

    /// <summary>A small tree of its own under the root: Sales with North under it, and Finance.</summary>
    private static async Task<(OrganisationUnit Root, OrganisationUnit Sales, OrganisationUnit North, OrganisationUnit Finance)> TreeAsync(AsyncServiceScope scope)
    {
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = await OrganisationUnitServiceTests.RootAsync(scope);
        var sales = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Sales");
        var north = await OrganisationUnitServiceTests.CreateAsync(units, sales.Id, "North");
        var finance = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Finance");
        return (root, sales, north, finance);
    }

    private static async Task AssignAsync(AsyncServiceScope scope, Guid principal, RoleScope roleScope)
    {
        var result = await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, SystemRoles.ReadOnly, roleScope, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }

    private static async Task<bool> AllowedAsync(IPermissionEvaluator evaluator, Guid principal, AccessScope scope)
    {
        var request = new AccessRequest(principal, ReadUnits, "GET /x", "/x") { Scope = scope };
        return (await evaluator.EvaluateAsync(request, TestContext.Current.CancellationToken)).Allowed;
    }

    private ServiceProvider Services() => fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), new RecordingAuditWriter());
}
