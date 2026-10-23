using Iron.Kernel.Data.Tests.Sample;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Data.Tests;

[Collection(PostgresCollection.Name)]
public class AuditStampInterceptorTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Organisation = Ids.New();

    [Fact]
    [Proves("ADR-0008")]
    public async Task AddingStampsCreatedAndUpdatedAndStartsTheVersionAtOne()
    {
        var actor = Ids.New();
        var time = new FakeTimeProvider(Start);
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(actor));

        var widget = new Widget(Organisation, "first");
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.Widgets.Add(widget);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            var saved = await context.Widgets.SingleAsync(w => w.Id == widget.Id, TestContext.Current.CancellationToken);

            Assert.Equal(Start, saved.CreatedAt);
            Assert.Equal(actor, saved.CreatedBy);
            Assert.Equal(Start, saved.UpdatedAt);
            Assert.Equal(actor, saved.UpdatedBy);
            Assert.Equal(1, saved.Version);
            Assert.Equal(Organisation, saved.OwningOrganisationId);
        }
    }

    [Fact]
    [Proves("TF-065 AC1", "ADR-0007")]
    public async Task ATimeSetOnARecordBeforeItIsSavedGivesWayToTheServersClock()
    {
        var time = new FakeTimeProvider(Start);
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(Ids.New()));

        // What a client's time would be if any code copied it onto the record: the stamp replaces it on save.
        var widget = new Widget(Organisation, "clocked");
        var claimed = Start.AddYears(-3);
        foreach (var name in new[] { "CreatedAt", "UpdatedAt" })
        {
            typeof(Iron.Kernel.Data.Entities.AuditedEntity).GetProperty(name)!.SetValue(widget, claimed);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.Widgets.Add(widget);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Widgets.AsNoTracking().SingleAsync(w => w.Id == widget.Id, TestContext.Current.CancellationToken);
            Assert.Equal((Start, Start), (saved.CreatedAt, saved.UpdatedAt));
        }
    }

    [Fact]
    [Proves("ADR-0008")]
    public async Task ChangingStampsUpdatedAndIncrementsTheVersionButKeepsCreated()
    {
        var creator = Ids.New();
        var editor = Ids.New();
        var time = new FakeTimeProvider(Start);
        var widget = new Widget(Organisation, "before");

        await using (var provider = fixture.BuildServices(time, new FixedPrincipal(creator)))
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.Widgets.Add(widget);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        time.Advance(TimeSpan.FromMinutes(5));
        await using (var provider = fixture.BuildServices(time, new FixedPrincipal(editor)))
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            var loaded = await context.Widgets.SingleAsync(w => w.Id == widget.Id, TestContext.Current.CancellationToken);
            loaded.Rename("after");
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(Start, loaded.CreatedAt);
            Assert.Equal(creator, loaded.CreatedBy);
            Assert.Equal(Start.AddMinutes(5), loaded.UpdatedAt);
            Assert.Equal(editor, loaded.UpdatedBy);
            Assert.Equal(2, loaded.Version);
        }
    }

    [Fact]
    [Proves("ADR-0008")]
    public async Task ASecondWriterOnAStaleVersionIsRejected()
    {
        var widget = new Widget(Organisation, "contended");
        await using var provider = fixture.BuildServices(new FakeTimeProvider(Start), new FixedPrincipal(Ids.New()));

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.Widgets.Add(widget);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var scopeA = provider.CreateAsyncScope();
        await using var scopeB = provider.CreateAsyncScope();
        var contextA = scopeA.ServiceProvider.GetRequiredService<SampleDbContext>();
        var contextB = scopeB.ServiceProvider.GetRequiredService<SampleDbContext>();
        var fromA = await contextA.Widgets.SingleAsync(w => w.Id == widget.Id, TestContext.Current.CancellationToken);
        var fromB = await contextB.Widgets.SingleAsync(w => w.Id == widget.Id, TestContext.Current.CancellationToken);

        fromA.Rename("a wins");
        await contextA.SaveChangesAsync(TestContext.Current.CancellationToken);

        fromB.Rename("b is stale");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0008")]
    public async Task DeletingASoftDeletableMarksItAndHidesItFromQueries()
    {
        var actor = Ids.New();
        var time = new FakeTimeProvider(Start);
        var widget = new Widget(Organisation, "to delete");
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(actor));

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.Widgets.Add(widget);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        time.Advance(TimeSpan.FromHours(1));
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            var loaded = await context.Widgets.SingleAsync(w => w.Id == widget.Id, TestContext.Current.CancellationToken);
            context.Widgets.Remove(loaded);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();

            Assert.False(await context.Widgets.AnyAsync(w => w.Id == widget.Id, TestContext.Current.CancellationToken));

            var marked = await context.Widgets.IgnoreQueryFilters().SingleAsync(w => w.Id == widget.Id, TestContext.Current.CancellationToken);
            Assert.True(marked.IsDeleted);
            Assert.Equal(Start.AddHours(1), marked.DeletedAt);
            Assert.Equal(actor, marked.DeletedBy);
            Assert.Equal(2, marked.Version);
        }
    }

    [Fact]
    [Proves("ADR-0008")]
    public async Task AppendOnlyRowsGetCreatedStampsAndRefuseChanges()
    {
        var actor = Ids.New();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(Start), new FixedPrincipal(actor));
        var row = new WidgetEvent(Ids.New(), "created");

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.WidgetEvents.Add(row);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var editScope = provider.CreateAsyncScope();
        var editContext = editScope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var loaded = await editContext.WidgetEvents.SingleAsync(e => e.Id == row.Id, TestContext.Current.CancellationToken);

        Assert.Equal(Start, loaded.CreatedAt);
        Assert.Equal(actor, loaded.CreatedBy);

        loaded.Edit("changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => editContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0008")]
    public void ABusinessRecordNeedsAnOwningOrganisation()
    {
        Assert.Throws<ArgumentException>(() => new Widget(Guid.Empty, "no owner"));
    }
}
