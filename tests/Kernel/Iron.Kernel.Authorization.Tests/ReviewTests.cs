using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>
/// Access reviews against PostgreSQL (TF-038, ADR-0037): the snapshot, reviewers limited to their units, a non-reviewer
/// refused, a revoke ending the assignment at once, the last-administrator guard, confirm-all, and the escalation of
/// overdue units to the campaign's owner on a fake clock.
/// </summary>
[Collection(AuthorizationCollection.Name)]
public class ReviewTests(AuthorizationFixture fixture)
{
    [Fact]
    public async Task StartingACampaignTakesASnapshotOfTheAssignmentsInItsUnitsWithTheirPaths()
    {
        var (north, store, south) = await TreeAsync();
        var (ada, grace, linus) = (fixture.Principals.Add(), fixture.Principals.Add(), fixture.Principals.Add());
        var role = await RoleAsync([AuthorizationPermissions.UnitsRead.Name]);
        await AssignAsync(ada, role, RoleScope.Unit(north.Id));
        await AssignAsync(grace, role, RoleScope.Unit(store.Id, includeChildren: true));
        await AssignAsync(linus, role, RoleScope.Unit(south.Id));
        var manager = await HolderAsync([AuthorizationPermissions.ReviewsManage.Name], RoleScope.Tenant);
        await using var scope = fixture.Scope(manager);
        var reviews = scope.Get<ReviewService>();

        var withChildren = await StartAsync(scope, new ReviewUnitInput(north.Id, IncludeChildren: true));
        var alone = await StartAsync(scope, new ReviewUnitInput(north.Id, IncludeChildren: false));
        await AssignAsync(linus, role, RoleScope.Unit(north.Id)); // after the start: not in the snapshot

        var items = await ItemsOfAsync(fixture, withChildren.Id);
        Assert.Equal(new[] { (ada, "North"), (grace, "Store 12") }.Order(), items.Select(i => (i.PrincipalId, i.UnitName)).Order());
        var stored = Assert.Single(items, i => i.PrincipalId == grace);
        Assert.Equal((role, RoleScopeKind.UnitWithChildren), (stored.RoleId, stored.ScopeKind));
        Assert.Equal([north.Id, store.Id], stored.UnitPath.Skip(1).Select(step => step.Id));
        Assert.Equal(["North", "Store 12"], stored.UnitPath.Skip(1).Select(step => step.Name));
        Assert.Equal([ada], (await ItemsOfAsync(fixture, alone.Id)).Select(i => i.PrincipalId));

        // The campaign lists principals and roles per unit, with its progress.
        Assert.Equal((2, 0, 2), (withChildren.Progress.Total, withChildren.Progress.Decided, withChildren.Progress.Open));
        Assert.Equal(["North", "Store 12"], withChildren.ByUnit.Select(u => u.UnitName));
        Assert.Equal((manager, ReviewStatus.Open), (withChildren.Owner.Id, withChildren.Status));
        Assert.Contains(scope.Audit.Events, e => e.Action == AuthorizationAuditEvents.ReviewStarted && e.RecordId == withChildren.Id);
        Assert.Equal(2, (await reviews.DescribeAsync(withChildren.Id, TestContext.Current.CancellationToken)).Value.Progress.Total);
    }

    [Fact]
    public async Task AReviewerSeesAndDecidesOnlyTheItemsOfTheirUnitsAndANonReviewerIsRefused()
    {
        var (north, store, _) = await TreeAsync();
        var (ada, grace) = (fixture.Principals.Add(), fixture.Principals.Add());
        var role = await RoleAsync([AuthorizationPermissions.UnitsRead.Name]);
        await AssignAsync(ada, role, RoleScope.Unit(north.Id));
        await AssignAsync(grace, role, RoleScope.Unit(store.Id));
        var review = await StartAsManagerAsync(new ReviewUnitInput(north.Id, IncludeChildren: true));
        var storeReviewer = await HolderAsync([AuthorizationPermissions.ReviewsDecide.Name], RoleScope.Unit(store.Id));
        var stranger = await HolderAsync([AuthorizationPermissions.UnitsRead.Name], RoleScope.Tenant);
        var all = await ItemsOfAsync(fixture, review.Id);
        var adasItem = all.Single(i => i.PrincipalId == ada).Id;

        await using var asReviewer = fixture.Scope(storeReviewer);
        var reviews = asReviewer.Get<ReviewService>();
        var visible = await (await reviews.ItemsAsync(review.Id, ItemState.All, TestContext.Current.CancellationToken)).ToListAsync(TestContext.Current.CancellationToken);
        var described = await reviews.DescribeAsync(review.Id, TestContext.Current.CancellationToken);
        var outside = await reviews.DecideAsync(review.Id, adasItem, ReviewDecision.Confirm, null, TestContext.Current.CancellationToken);
        var listed = await (await reviews.VisibleAsync(TestContext.Current.CancellationToken))!.Select(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([grace], visible.Select(i => i.PrincipalId));
        Assert.Equal(1, described.Value.Progress.Total);
        Assert.Equal(["Store 12"], described.Value.ByUnit.Select(u => u.UnitName));
        Assert.False(described.Value.CanManage);
        Assert.Equal(AuthorizationErrors.ReviewItemNotFound, outside.Error.Code);
        Assert.Contains(review.Id, listed);

        await using var asStranger = fixture.Scope(stranger);
        var strangers = asStranger.Get<ReviewService>();
        Assert.Null(await strangers.VisibleAsync(TestContext.Current.CancellationToken));
        var refusal = await strangers.RefusalAsync("GET /api/v1/access-reviews", TestContext.Current.CancellationToken);
        Assert.Equal((ErrorKind.Forbidden, PermissionErrors.PermissionDenied), (refusal.Kind, refusal.Code));
        Assert.Contains(asStranger.Audit.Events, e => e.Action == PermissionEvaluator.DenialAction && e.PrincipalId == stranger);
    }

    [Fact]
    public async Task ARevokeEndsTheAssignmentAtOnceAConfirmKeepsItAndEachItemIsDecidedOnce()
    {
        var (north, _, _) = await TreeAsync();
        var (ada, grace) = (fixture.Principals.Add(), fixture.Principals.Add());
        var role = await RoleAsync([AuthorizationPermissions.UnitsRead.Name]);
        await AssignAsync(ada, role, RoleScope.Unit(north.Id));
        await AssignAsync(grace, role, RoleScope.Unit(north.Id));
        var review = await StartAsManagerAsync(new ReviewUnitInput(north.Id, IncludeChildren: false));
        var reviewer = await HolderAsync([AuthorizationPermissions.ReviewsDecide.Name], RoleScope.Unit(north.Id));
        var items = await ItemsOfAsync(fixture, review.Id);

        await using var scope = fixture.Scope(reviewer);
        var reviews = scope.Get<ReviewService>();
        var grants = scope.Get<AccessGrants>();
        Assert.NotEmpty((await grants.OfAsync(ada, TestContext.Current.CancellationToken)).Grants); // warms the cache the revoke must clear

        var revoked = await reviews.DecideAsync(review.Id, items.Single(i => i.PrincipalId == ada).Id, ReviewDecision.Revoke, "Left the team.", TestContext.Current.CancellationToken);
        var confirmed = await reviews.DecideAsync(review.Id, items.Single(i => i.PrincipalId == grace).Id, ReviewDecision.Confirm, null, TestContext.Current.CancellationToken);
        var again = await reviews.DecideAsync(review.Id, items.Single(i => i.PrincipalId == grace).Id, ReviewDecision.Revoke, null, TestContext.Current.CancellationToken);

        Assert.True(revoked.IsSuccess, revoked.IsFailure ? revoked.Error.Message : null);
        Assert.Equal((ReviewDecision.Revoke, "Left the team.", true, reviewer), (revoked.Value.Decision!.Decision, revoked.Value.Decision.Comment, revoked.Value.Decision.AssignmentRemoved, revoked.Value.Decision.DecidedBy.Id));
        Assert.Empty((await grants.OfAsync(ada, TestContext.Current.CancellationToken)).Grants);
        var adasAssignment = items.Single(i => i.PrincipalId == ada).AssignmentId;
        Assert.False(await scope.Get<AuthorizationDbContext>().RoleAssignments.AnyAsync(a => a.Id == adasAssignment, TestContext.Current.CancellationToken));
        Assert.True(confirmed.IsSuccess);
        Assert.True(await scope.Get<AuthorizationDbContext>().RoleAssignments.AnyAsync(a => a.PrincipalId == grace, TestContext.Current.CancellationToken));
        Assert.Equal(AuthorizationErrors.ReviewItemDecided, again.Error.Code);
        var unassigned = Assert.Single(scope.Audit.Events, e => e.Action == AuthorizationAuditEvents.RoleUnassigned);
        Assert.Equal(review.Id.ToString("D"), unassigned.Details!["access_review_id"]);
        Assert.Equal(2, scope.Audit.Events.Count(e => e.Action == AuthorizationAuditEvents.ReviewDecided));

        var progress = (await reviews.DescribeAsync(review.Id, TestContext.Current.CancellationToken)).Value.Progress;
        Assert.Equal((2, 2, 1, 1, 0), (progress.Total, progress.Decided, progress.Confirmed, progress.Revoked, progress.Open));
    }

    [Fact]
    public async Task ARevokeKeepsTheLastTenantAdministratorAndSaysThatIsWhy()
    {
        var tenant = await fixture.CreateTenantDatabaseAsync($"review_{Ids.New():N}");
        var administrator = fixture.Principals.Add();
        await using (var setup = fixture.Scope(SystemPrincipal.SystemId, connectionString: tenant))
        {
            await RoleServiceTests.AssignAsync(setup.Scope, administrator, SystemRoles.TenantAdministrator);
        }

        await using var scope = fixture.Scope(administrator, connectionString: tenant);
        var reviews = scope.Get<ReviewService>();
        var root = await OrganisationUnitServiceTests.RootAsync(scope.Scope);
        var started = await reviews.StartAsync(new StartReviewInput("The whole tenant", Today(new FakeTimeProvider()).AddDays(14), [new ReviewUnitInput(root.Id, IncludeChildren: false)]), TestContext.Current.CancellationToken);
        var item = await (await reviews.ItemsAsync(started.Value.Id, ItemState.All, TestContext.Current.CancellationToken)).SingleAsync(i => i.PrincipalId == administrator, TestContext.Current.CancellationToken);

        var refused = await reviews.DecideAsync(started.Value.Id, item.Id, ReviewDecision.Revoke, null, TestContext.Current.CancellationToken);

        Assert.Equal((Scopes.Tenant, root.Id), (item.ScopeKind, item.UnitId));
        Assert.Equal((ErrorKind.Conflict, AuthorizationErrors.AssignmentLastAdministrator), (refused.Error.Kind, refused.Error.Code));
        Assert.True(await scope.Get<AuthorizationDbContext>().RoleAssignments.AnyAsync(a => a.Id == item.AssignmentId, TestContext.Current.CancellationToken));
        Assert.Equal(1, (await reviews.DescribeAsync(started.Value.Id, TestContext.Current.CancellationToken)).Value.Progress.Open);
    }

    [Fact]
    public async Task ConfirmAllConfirmsAPageLeavesDecidedItemsAloneAndAClosedCampaignTakesNoDecisions()
    {
        var (north, _, _) = await TreeAsync();
        var role = await RoleAsync([AuthorizationPermissions.UnitsRead.Name]);
        var people = Enumerable.Range(0, 3).Select(_ => fixture.Principals.Add()).ToList();
        foreach (var person in people)
        {
            await AssignAsync(person, role, RoleScope.Unit(north.Id));
        }

        var manager = await HolderAsync([AuthorizationPermissions.ReviewsManage.Name, AuthorizationPermissions.ReviewsDecide.Name], RoleScope.Tenant);
        await using var scope = fixture.Scope(manager);
        var reviews = scope.Get<ReviewService>();
        var review = await StartAsync(scope, new ReviewUnitInput(north.Id, IncludeChildren: false));
        var items = await ItemsOfAsync(fixture, review.Id);
        Assert.True((await reviews.DecideAsync(review.Id, items[0].Id, ReviewDecision.Revoke, null, TestContext.Current.CancellationToken)).IsSuccess);

        var many = await reviews.ConfirmManyAsync(review.Id, [.. items.Select(i => i.Id)], "Checked with the team lead.", TestContext.Current.CancellationToken);
        var foreign = await reviews.ConfirmManyAsync(review.Id, [Ids.New()], null, TestContext.Current.CancellationToken);
        var closed = await reviews.CloseAsync(review.Id, TestContext.Current.CancellationToken);
        var late = await reviews.DecideAsync(review.Id, items[1].Id, ReviewDecision.Revoke, null, TestContext.Current.CancellationToken);
        var closedAgain = await reviews.CloseAsync(review.Id, TestContext.Current.CancellationToken);

        Assert.Equal((2, 1), (many.Value.Confirmed, many.Value.AlreadyDecided));
        Assert.Equal(AuthorizationErrors.ReviewItemNotFound, foreign.Error.Code);
        Assert.Equal((ReviewStatus.Closed, 3, 0), (closed.Value.Status, closed.Value.Progress.Decided, closed.Value.Progress.Open));
        Assert.Equal(AuthorizationErrors.ReviewClosed, late.Error.Code);
        Assert.Equal(AuthorizationErrors.ReviewClosed, closedAgain.Error.Code);
        Assert.Contains(scope.Audit.Events, e => e.Action == AuthorizationAuditEvents.ReviewClosed && e.RecordId == review.Id);
    }

    [Fact]
    public async Task AUnitSevenDaysOverdueIsEscalatedOnceToTheCampaignOwner()
    {
        var (north, store, _) = await TreeAsync();
        var (ada, grace) = (fixture.Principals.Add(), fixture.Principals.Add());
        var role = await RoleAsync([AuthorizationPermissions.UnitsRead.Name]);
        await AssignAsync(ada, role, RoleScope.Unit(north.Id));
        await AssignAsync(grace, role, RoleScope.Unit(store.Id));
        var storeReviewer = await HolderAsync([AuthorizationPermissions.ReviewsDecide.Name], RoleScope.Unit(store.Id));
        var manager = await HolderAsync([AuthorizationPermissions.ReviewsManage.Name, AuthorizationPermissions.ReviewsDecide.Name], RoleScope.Tenant);
        var time = new FakeTimeProvider();
        await using var scope = fixture.Scope(manager, time);
        var reviews = scope.Get<ReviewService>();
        var pass = scope.Get<ReviewEscalationPass>();
        var started = await reviews.StartAsync(new StartReviewInput("Quarterly review", Today(time).AddDays(2), [new ReviewUnitInput(north.Id, IncludeChildren: true)]), TestContext.Current.CancellationToken);
        var review = started.Value;
        var items = await ItemsOfAsync(fixture, review.Id);
        Assert.True((await reviews.DecideAsync(review.Id, items.Single(i => i.PrincipalId == ada).Id, ReviewDecision.Confirm, null, TestContext.Current.CancellationToken)).IsSuccess);

        time.Advance(TimeSpan.FromDays(2 + 6)); // six days overdue: not yet
        await pass.RunAsync(TestContext.Current.CancellationToken);
        var early = await EscalationsAsync(scope, review.Id);
        var overdue = (await reviews.DescribeAsync(review.Id, TestContext.Current.CancellationToken)).Value;
        time.Advance(TimeSpan.FromDays(1));
        await pass.RunAsync(TestContext.Current.CancellationToken);
        await pass.RunAsync(TestContext.Current.CancellationToken);
        var escalations = await EscalationsAsync(scope, review.Id);
        var shown = (await reviews.DescribeAsync(review.Id, TestContext.Current.CancellationToken)).Value;

        Assert.Empty(early);
        Assert.True(overdue.Overdue);
        Assert.Equal((2, 2), (overdue.Progress.Open, overdue.Progress.Overdue)); // Grace and the store's reviewer, whose own role is in the snapshot

        // North is decided; Store 12 is not, so it alone is escalated, once, to the owner, naming its reviewers.
        var escalation = Assert.Single(escalations);
        Assert.Equal((store.Id, 2, 7), (escalation.UnitId, escalation.OpenItems, escalation.DaysOverdue));
        Assert.Equal([manager], escalation.Targets);
        Assert.Contains(storeReviewer, escalation.Reviewers);
        Assert.Contains(manager, escalation.Reviewers);
        var audited = Assert.Single(scope.Audit.Events, e => e.Action == AuthorizationAuditEvents.ReviewEscalated && e.RecordId == review.Id);
        Assert.Equal(manager.ToString("D"), audited.Details!["targets"]);
        var storeShown = Assert.Single(shown.ByUnit, u => u.UnitId == store.Id);
        Assert.Equal([manager], storeShown.Escalation!.Targets.Select(t => t.Id));
        Assert.Null(Assert.Single(shown.ByUnit, u => u.UnitId == north.Id).Escalation);
    }

    [Fact]
    public async Task TheKernelEscalationNamesTheOwnerAndADueDateThatHasPassedIsRefused()
    {
        var owner = Ids.New();
        var targets = await new CampaignOwnerEscalation().TargetsAsync(new OverdueReview(Ids.New(), "Review", owner, Ids.New(), "North", new DateOnly(2026, 10, 1), 7, 3, []), TestContext.Current.CancellationToken);
        Assert.Equal([owner], targets);

        var (north, _, _) = await TreeAsync();
        var manager = await HolderAsync([AuthorizationPermissions.ReviewsManage.Name], RoleScope.Tenant);
        var time = new FakeTimeProvider();
        await using var scope = fixture.Scope(manager, time);
        var late = await scope.Get<ReviewService>().StartAsync(new StartReviewInput("Late", Today(time).AddDays(-1), [new ReviewUnitInput(north.Id, IncludeChildren: false)]), TestContext.Current.CancellationToken);
        var nowhere = await scope.Get<ReviewService>().StartAsync(new StartReviewInput("Nowhere", Today(time), [new ReviewUnitInput(Ids.New(), IncludeChildren: false)]), TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationErrors.ReviewDuePassed, late.Error.Code);
        Assert.Equal(AuthorizationErrors.UnitNotFound, nowhere.Error.Code);
    }

    private static DateOnly Today(FakeTimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private static async Task<ReviewView> StartAsync(TestScope scope, params ReviewUnitInput[] units)
    {
        var started = await scope.Get<ReviewService>().StartAsync(new StartReviewInput("Quarterly review", Today(new FakeTimeProvider()).AddDays(14), units), TestContext.Current.CancellationToken);
        Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);
        return started.Value;
    }

    private async Task<ReviewView> StartAsManagerAsync(params ReviewUnitInput[] units)
    {
        var manager = await HolderAsync([AuthorizationPermissions.ReviewsManage.Name], RoleScope.Tenant);
        await using var scope = fixture.Scope(manager);
        return await StartAsync(scope, units);
    }

    /// <summary>Every item of a campaign, as someone who decides everywhere sees them.</summary>
    private static async Task<IReadOnlyList<ReviewItemView>> ItemsOfAsync(AuthorizationFixture fixture, Guid reviewId)
    {
        await using var scope = fixture.Scope(SystemPrincipal.SystemId);
        var context = scope.Get<AuthorizationDbContext>();
        var items = await context.AccessReviewItems.AsNoTracking().Where(i => i.ReviewId == reviewId).OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).ToListAsync(TestContext.Current.CancellationToken);
        return await scope.Get<ReviewService>().DescribeItemsAsync(items, TestContext.Current.CancellationToken);
    }

    private static async Task<List<AccessReviewEscalation>> EscalationsAsync(TestScope scope, Guid reviewId) =>
        await scope.Get<AuthorizationDbContext>().AccessReviewEscalations.AsNoTracking().Where(e => e.ReviewId == reviewId).ToListAsync(TestContext.Current.CancellationToken);

    /// <summary>North with Store 12 below it, and South beside it, under the root: new units for every test.</summary>
    private async Task<(OrganisationUnit North, OrganisationUnit Store, OrganisationUnit South)> TreeAsync()
    {
        await using var scope = fixture.Scope(SystemPrincipal.SystemId);
        var units = scope.Get<OrganisationUnitService>();
        var root = await OrganisationUnitServiceTests.RootAsync(scope.Scope);
        var north = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "North");
        var store = await OrganisationUnitServiceTests.CreateAsync(units, north.Id, "Store 12");
        var south = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "South");
        return (north, store, south);
    }

    private async Task<Guid> RoleAsync(IReadOnlyList<string> permissions)
    {
        await using var setup = fixture.Scope(SystemPrincipal.SystemId);
        var context = setup.Get<AuthorizationDbContext>();
        var role = new Role(RoleServiceTests.Code(), "Review test role", string.Empty, isSystem: false, PrincipalKind.User, permissions);
        context.Roles.Add(role);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return role.Id;
    }

    private async Task AssignAsync(Guid principal, Guid roleId, RoleScope scope)
    {
        await using var setup = fixture.Scope(SystemPrincipal.SystemId);
        var context = setup.Get<AuthorizationDbContext>();
        context.RoleAssignments.Add(new RoleAssignment(roleId, principal, scope));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> HolderAsync(IReadOnlyList<string> permissions, RoleScope scope)
    {
        var principal = fixture.Principals.Add();
        await AssignAsync(principal, await RoleAsync(permissions), scope);
        return principal;
    }
}
