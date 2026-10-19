using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Data.Tests.Sample;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Data.Tests;

/// <summary>ADR-0030 in the data layer: the list keeps what one permission reaches; a record's owner must be an active unit.</summary>
[Collection(PostgresCollection.Name)]
public class RecordAccessDatabaseTests(PostgresFixture fixture)
{
    private static readonly Guid UnitA = Ids.New();
    private static readonly Guid UnitB = Ids.New();
    private static readonly Guid Closed = Ids.New();

    [Fact]
    public async Task AListKeepsTheUnitsRecordsAndTheRecordsGrantedOneByOneInOneQuery()
    {
        var prefix = $"access-{Ids.New():N}";
        var (inA, inB, granted) = await SeedAsync(prefix);

        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        await using var scope = provider.CreateAsyncScope();
        var widgets = scope.ServiceProvider.GetRequiredService<SampleDbContext>().Widgets.AsNoTracking().Where(w => w.Name.StartsWith(prefix));

        var everything = await widgets.WhereAccessible(RecordAccess.Everything).Select(w => w.Id).ToListAsync(TestContext.Current.CancellationToken);
        var within = widgets.WhereAccessible(RecordAccess.Within([UnitA], [granted]));
        var reached = await within.Select(w => w.Id).ToListAsync(TestContext.Current.CancellationToken);
        var nothing = await widgets.WhereAccessible(RecordAccess.Nothing).CountAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, everything.Count);
        Assert.Equal(new[] { inA, granted }.Order(), reached.Order());
        Assert.DoesNotContain(inB, reached);
        Assert.Equal(0, nothing);
        Assert.Contains("= ANY (", within.ToQueryString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordOwnedByAUnitThatIsNotActiveIsRefusedOnSave()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), services => services.AddSingleton<IOrganisationUnitCheck>(new OnlyClosedIsInactive()));
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();

        context.Widgets.Add(new Widget(UnitA, $"owned-{Ids.New():N}"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Widgets.Add(new Widget(Closed, $"closed-{Ids.New():N}"));
        var refused = await Assert.ThrowsAsync<OwningOrganisationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal([Closed], refused.UnitIds);
        Assert.Contains(Closed.ToString(), refused.Message, StringComparison.Ordinal);
    }

    private async Task<(Guid InA, Guid InB, Guid Granted)> SeedAsync(string prefix)
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var inA = new Widget(UnitA, $"{prefix}-a");
        var inB = new Widget(UnitB, $"{prefix}-b");
        var granted = new Widget(UnitB, $"{prefix}-granted");
        context.Widgets.AddRange(inA, inB, granted);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (inA.Id, inB.Id, granted.Id);
    }

    private sealed class OnlyClosedIsInactive : IOrganisationUnitCheck
    {
        public Task<IReadOnlyList<Guid>> NotActiveAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>(unitIds.Where(id => id == Closed).ToList());
    }
}
