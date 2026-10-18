using Iron.Data.Tests.Sample;
using Iron.Data.Transactions;
using Iron.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Data.Tests.Transactions;

/// <summary>One transaction for the whole scope (TF-060 AC1): everything in it lands together or not at all.</summary>
[Collection(PostgresCollection.Name)]
public class UnitOfWorkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DisposingWithoutCommitRollsBackTheSavedChanges()
    {
        await using var provider = Build();
        var key = "uow:" + Ids.New();

        await using (var scope = provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            await using (await unitOfWork.BeginAsync(TestContext.Current.CancellationToken))
            {
                context.Preferences.Add(new Preference(key, "pending"));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
                Assert.True(unitOfWork.IsActive);
            }

            Assert.False(unitOfWork.IsActive);
        }

        Assert.False(await Exists(provider, key));
    }

    [Fact]
    public async Task CommitMakesTheSavedChangesVisible()
    {
        await using var provider = Build();
        var key = "uow:" + Ids.New();

        await using (var scope = provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            await using var transaction = await unitOfWork.BeginAsync(TestContext.Current.CancellationToken);
            context.Preferences.Add(new Preference(key, "committed"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(await Exists(provider, key));
    }

    [Fact]
    public async Task AnInnerScopeLeftWithoutCommitForcesTheWholeTransactionToRollBack()
    {
        await using var provider = Build();
        var key = "uow:" + Ids.New();

        await using (var scope = provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            await using var outer = await unitOfWork.BeginAsync(TestContext.Current.CancellationToken);
            context.Preferences.Add(new Preference(key, "outer"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await using (await unitOfWork.BeginAsync(TestContext.Current.CancellationToken))
            {
                // an inner piece of work gives up without Commit
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => outer.CommitAsync(TestContext.Current.CancellationToken));
        }

        Assert.False(await Exists(provider, key));
    }

    private ServiceProvider Build() => fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));

    private static async Task<bool> Exists(ServiceProvider provider, string key)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        return await context.Preferences.AsNoTracking().AnyAsync(p => p.Key == key, TestContext.Current.CancellationToken);
    }
}
