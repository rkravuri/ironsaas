using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data.Visibility;
using Iron.Kernel.Visibility.Contracts;
using Iron.Kernel.Visibility.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Visibility.Tests;

/// <summary>
/// Access reviews and visibility tags against PostgreSQL (TF-241 AC7, TF-038, ADR-0057): a review of role assignments
/// lists the tags each person holds, and a review of tag holders confirms a holding or revokes it, which takes the tag
/// away at once through the Visibility module, audited on the person's record.
/// </summary>
[Collection(VisibilityCollection.Name)]
public class TagReviewTests(VisibilityFixture fixture)
{
    private readonly TestPrincipals _people = new();

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-241 AC7", "TF-038 AC1")]
    public async Task AReviewOfRoleAssignmentsListsTheTagsEachPersonHolds()
    {
        var (reviewer, unit) = await SetUpAsync();
        var person = _people.Add("Tagged reader", homeUnit: unit);
        var untagged = _people.Add("Untagged reader", homeUnit: unit);
        var (apac, latam) = (await TagAsync(reviewer, "Apac"), await TagAsync(reviewer, "Latam"));
        await HoldAsync(reviewer, person, latam, apac);
        await GrantAsync(person, unit);
        await GrantAsync(untagged, unit);

        await using var provider = fixture.BuildServices(_people, new FixedPrincipal(reviewer));
        await using var scope = provider.CreateAsyncScope();
        var reviews = scope.ServiceProvider.GetRequiredService<ReviewService>();
        var started = await reviews.StartAsync(new StartReviewInput("Roles with tags", Today, [new ReviewUnitInput(unit, false)]), Cancel);
        var items = await ItemsAsync(reviews, started.Value.Id);

        Assert.Equal(ReviewSubject.Roles, started.Value.Subject);
        Assert.Equal(new[] { apac, latam }.Order(StringComparer.Ordinal), items.Single(i => i.PrincipalId == person).HeldTags.Select(t => t.Code));
        Assert.Empty(items.Single(i => i.PrincipalId == untagged).HeldTags);
        Assert.All(items, i => Assert.Null(i.Tag));
    }

    [Fact]
    [Proves("TF-038 AC2", "TF-241 AC7", "ADR-0057")]
    public async Task AReviewOfTagHoldersConfirmsAHoldingOrRevokesItWhichTakesTheTagAwayAtOnce()
    {
        var (reviewer, unit) = await SetUpAsync();
        var person = _people.Add("Holds two", homeUnit: unit);
        var other = _people.Add("Holds one", homeUnit: unit);
        var elsewhere = _people.Add("Holds one elsewhere", homeUnit: Ids.New());
        var (apac, latam) = (await TagAsync(reviewer, "Apac"), await TagAsync(reviewer, "Latam"));
        await HoldAsync(reviewer, person, apac, latam);
        await HoldAsync(reviewer, other, apac);
        await HoldAsync(reviewer, elsewhere, apac);

        await using var provider = fixture.BuildServices(_people, new FixedPrincipal(reviewer));
        await using var scope = provider.CreateAsyncScope();
        var reviews = scope.ServiceProvider.GetRequiredService<ReviewService>();
        var started = await reviews.StartAsync(new StartReviewInput("Tag holders", Today, [new ReviewUnitInput(unit, false)], ReviewSubject.VisibilityTags), Cancel);
        var items = await ItemsAsync(reviews, started.Value.Id);
        var revoke = items.Single(i => i.PrincipalId == person && i.Tag!.Code == latam);
        var keep = items.Single(i => i.PrincipalId == other);
        var revoked = await reviews.DecideAsync(started.Value.Id, revoke.Id, ReviewDecision.Revoke, "No longer in Latin America.", "test", Cancel);
        var kept = await reviews.DecideAsync(started.Value.Id, keep.Id, ReviewDecision.Confirm, null, "test", Cancel);

        Assert.Equal(ReviewSubject.VisibilityTags, started.Value.Subject);
        Assert.Equal(3, items.Count);
        Assert.DoesNotContain(items, i => i.PrincipalId == elsewhere);
        Assert.All(items, i => Assert.Null(i.RoleId));
        Assert.Equal(new[] { apac, latam }.Order(StringComparer.Ordinal), revoke.HeldTags.Select(t => t.Code));
        Assert.True(revoked.Value.Decision!.AssignmentRemoved);
        Assert.False(kept.Value.Decision!.AssignmentRemoved);

        var tags = scope.ServiceProvider.GetRequiredService<IVisibilityTags>();
        Assert.Equal([apac], await tags.HeldAsync(person, Cancel));
        Assert.Equal([apac], await tags.HeldAsync(other, Cancel));
        var audited = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.RecordId == person && e.EventType == VisibilityAuditEvents.HolderTagsChanged)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Select(e => e.Details)
            .FirstAsync(Cancel);
        Assert.Contains(started.Value.Id.ToString("D"), audited, StringComparison.Ordinal);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>A reviewer who administers the tenant and holds no tags, and a unit of their own for the people reviewed.</summary>
    private async Task<(Guid Reviewer, Guid Unit)> SetUpAsync()
    {
        var reviewer = _people.Add("Reviewer");
        await using var provider = fixture.BuildServices(_people, SystemPrincipal.Instance);
        await using var scope = provider.CreateAsyncScope();
        var root = (await scope.ServiceProvider.GetRequiredService<IOrganisationUnits>().RootAsync(Cancel))!;
        var created = await scope.ServiceProvider.GetRequiredService<OrganisationUnitService>().CreateAsync(root.Id, "Reviewed", $"reviewed-{Ids.New():N}"[..30], KernelOrganisationKinds.Unit, Cancel);
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
        Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(reviewer, SystemRoles.TenantAdministrator, Cancel)).IsSuccess);
        return (reviewer, created.Value.Id);
    }

    private async Task GrantAsync(Guid principal, Guid unit)
    {
        await using var provider = fixture.BuildServices(_people, SystemPrincipal.Instance);
        await using var scope = provider.CreateAsyncScope();
        var granted = await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, SystemRoles.ReadOnly, RoleScope.Unit(unit), Cancel);
        Assert.True(granted.IsSuccess, granted.IsFailure ? granted.Error.Message : null);
    }

    private async Task<string> TagAsync(Guid admin, string prefix)
    {
        await using var provider = fixture.BuildServices(_people, new FixedPrincipal(admin));
        await using var scope = provider.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<TagService>().CreateAsync(VisibilityFixture.Code(prefix), prefix, null, Cancel);
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
        return created.Value.Code;
    }

    private async Task HoldAsync(Guid admin, Guid principal, params string[] codes)
    {
        await using var provider = fixture.BuildServices(_people, new FixedPrincipal(admin));
        await using var scope = provider.CreateAsyncScope();
        var set = await scope.ServiceProvider.GetRequiredService<HoldingService>().SetAsync(principal, "Someone", codes, Cancel);
        Assert.True(set.IsSuccess, set.IsFailure ? set.Error.Message : null);
    }

    private async Task<IReadOnlyList<ReviewItemView>> ItemsAsync(ReviewService reviews, Guid reviewId)
    {
        var items = await reviews.ItemsAsync(reviewId, ItemState.All, "test", Cancel);
        Assert.True(items.IsSuccess, items.IsFailure ? items.Error.Message : null);
        return await reviews.DescribeItemsAsync(await items.Value.ToListAsync(Cancel), Cancel);
    }
}
