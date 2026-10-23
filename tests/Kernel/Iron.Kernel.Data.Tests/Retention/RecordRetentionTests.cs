using Iron.Kernel.Data.Retention;
using Iron.Kernel.Data.Tests.Sample;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Data.Tests.Retention;

/// <summary>
/// The retention job on the data tests' sample module (TF-066, ADR-0050): an order past its class's period is archived and
/// then deleted with an audit event; one whose supplier is under a legal hold is skipped and stays; one still inside its
/// period stays; a hold on the whole tenant keeps everything.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RecordRetentionTests(PostgresFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-066 AC1", "TF-066 AC2", "ADR-0050")]
    public async Task AnOrderPastItsRetentionIsArchivedThenDeletedWithAnEventAndAHeldSuppliersOrdersStay()
    {
        var now = new DateTimeOffset(2034, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var held = Ids.New();
        var free = Ids.New();
        var unit = Ids.New();
        var archive = new RecordingArchive();
        var audit = new RecordingAudit();
        await using var provider = Services(time, HeldRecords.None with { Masters = new HashSet<(string, Guid)> { (SampleOrderRetention.SupplierType, held) } }, archive, audit);

        var old = now.AddYears(-8);
        var (heldOrder, freeOrder, recentOrder) = (new SampleOrder(unit, held, "held-8y", old), new SampleOrder(unit, free, "free-8y", old), new SampleOrder(unit, free, "free-1y", now.AddYears(-1)));
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.Orders.AddRange(heldOrder, freeOrder, recentOrder);
            await context.SaveChangesAsync(Cancel);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var runs = await scope.ServiceProvider.GetRequiredService<RecordRetentionRunner>().RunAsync(Cancel);
            var run = Assert.Single(runs);
            Assert.Equal((SampleOrderRetention.Type, RetentionClasses.BusinessDocument.Code, RetentionClasses.BusinessDocument.DefaultDays), (run.RecordType, run.RetentionClass, run.Days));
            Assert.True(run.Deleted >= 1 && run.Held >= 1);
        }

        // Archived first, then deleted, with an audit event naming the archive copy.
        Assert.Contains(archive.Archived, r => r.Id == freeOrder.Id);
        Assert.DoesNotContain(archive.Archived, r => r.Id == heldOrder.Id || r.Id == recentOrder.Id);
        var expired = Assert.Single(audit.Expired, e => e.RecordId == freeOrder.Id);
        Assert.DoesNotContain(audit.Expired, e => e.RecordId == heldOrder.Id || e.RecordId == recentOrder.Id);
        Assert.Equal((SampleOrderRetention.Type, freeOrder.Id, $"archive/{freeOrder.Id:D}"), (expired.RecordType, expired.RecordId, expired.ArchiveKey));
        await using (var scope = provider.CreateAsyncScope())
        {
            var left = await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Orders.AsNoTracking()
                .Where(o => o.Id == heldOrder.Id || o.Id == freeOrder.Id || o.Id == recentOrder.Id).Select(o => o.Reference).OrderBy(r => r).ToListAsync(Cancel);
            Assert.Equal(["free-1y", "held-8y"], left);
        }
    }

    [Fact]
    [Proves("TF-066 AC2", "ADR-0050")]
    public async Task AHoldOnTheWholeTenantKeepsEveryRecord()
    {
        var now = new DateTimeOffset(2034, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var archive = new RecordingArchive();
        var audit = new RecordingAudit();
        await using var provider = Services(new FakeTimeProvider(now), HeldRecords.None with { Tenant = true }, archive, audit);
        var order = new SampleOrder(Ids.New(), Ids.New(), "tenant-held", now.AddYears(-9));
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.Orders.Add(order);
            await context.SaveChangesAsync(Cancel);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RecordRetentionRunner>().RunAsync(Cancel);
            Assert.True(await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Orders.AnyAsync(o => o.Id == order.Id, Cancel));
        }

        Assert.DoesNotContain(archive.Archived, r => r.Id == order.Id);
        Assert.DoesNotContain(audit.Expired, e => e.RecordId == order.Id);
    }

    private ServiceProvider Services(FakeTimeProvider time, HeldRecords held, RecordingArchive archive, RecordingAudit audit) =>
        fixture.BuildServices(time, new FixedPrincipal(SystemPrincipal.SystemId), services =>
        {
            services.AddRetainedRecords<SampleOrderRetention>();
            services.AddSingleton<ILegalHolds>(new FixedHolds(held));
            services.AddSingleton<IRecordArchive>(archive);
            services.AddSingleton<IRetentionAudit>(audit);
            services.AddSingleton<IRetentionPeriods>(new DefaultPeriods());
            services.AddScoped<RecordRetentionRunner>();
        });

    private sealed class FixedHolds(HeldRecords held) : ILegalHolds
    {
        public Task<HeldRecords> ActiveAsync(CancellationToken cancellationToken) => Task.FromResult(held);
    }

    private sealed class DefaultPeriods : IRetentionPeriods
    {
        public Task<int> DaysAsync(RetentionClass retention, CancellationToken cancellationToken) => Task.FromResult(retention.DefaultDays);
    }

    private sealed class RecordingArchive : IRecordArchive
    {
        public List<RetainedRecord> Archived { get; } = [];

        public Task<string> ArchiveAsync(string recordType, RetentionClass retention, RetainedRecord record, CancellationToken cancellationToken)
        {
            Archived.Add(record);
            return Task.FromResult($"archive/{record.Id:D}");
        }
    }

    private sealed class RecordingAudit : IRetentionAudit
    {
        public List<RecordExpired> Expired { get; } = [];

        public Task ExpiredAsync(RecordExpired expired, CancellationToken cancellationToken)
        {
            Expired.Add(expired);
            return Task.CompletedTask;
        }
    }
}
