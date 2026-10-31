using System.Diagnostics;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Events.Data;
using Iron.Kernel.Events.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Events.Tests;

/// <summary>The outbox (TF-085, ADR-0060): an event commits with its change and its audit event, or not at all.</summary>
[Collection(EventsCollection.Name)]
public class OutboxTests(EventsFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("ADR-0060")]
    public async Task AnEventCommitsWithTheChangeAndItsAuditEvent()
    {
        var tenant = await fixture.NewTenantAsync();
        var actor = Ids.New();
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow), actor);
        var order = Ids.New();

        await using (var scope = provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginAsync(Cancel);
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEvent("probe.order_placed", AuditOutcome.Success, actor, RecordType: "probe_order", RecordId: order), Cancel);
            await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, actor), Cancel);
            await transaction.CommitAsync(Cancel);
        }

        await using var read = provider.CreateAsyncScope();
        var stored = await read.ServiceProvider.GetRequiredService<EventsDbContext>().Outbox.AsNoTracking().SingleAsync(Cancel);
        Assert.Equal(("probe.order.placed", 1, "probe_order", order, actor, "acme"), (stored.Type, stored.EventVersion, stored.RecordType, stored.RecordId, stored.ActorId, stored.Tenant));
        Assert.Equal(order, System.Text.Json.JsonDocument.Parse(stored.Data).RootElement.GetProperty("order_id").GetGuid());
        Assert.Null(stored.FannedAt);
        Assert.Equal(1, await tenant.AuditCountAsync("probe.order_placed", order));
    }

    [Fact]
    [Proves("ADR-0060")]
    public async Task ARolledBackChangeLeavesNoEvent()
    {
        var tenant = await fixture.NewTenantAsync();
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));

        await using (var scope = provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginAsync(Cancel);
            var order = Ids.New();
            await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()), Cancel);

            // Disposed without a commit: the change, and with it the event, never happened.
        }

        Assert.Equal(0, await EventsTenant.OutboxCountAsync(provider));
        Assert.DoesNotContain(await EventsTenant.DrainAsync(provider), p => p.DidWork);
    }

    [Fact]
    [Proves("ADR-0060")]
    public async Task AnEventIsPublishedOnlyInsideAUnitOfWork()
    {
        var tenant = await fixture.NewTenantAsync();
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var scope = provider.CreateAsyncScope();
        var order = Ids.New();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()), Cancel));

        Assert.Contains("inside the unit of work", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await EventsTenant.OutboxCountAsync(provider));
    }

    [Fact]
    [Proves("TF-110 AC2")]
    public async Task DataThatDoesNotFitItsDeclarationIsRefusedAndNothingIsWritten()
    {
        var tenant = await fixture.NewTenantAsync();
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using (var transaction = await unitOfWork.BeginAsync(Cancel))
        {
            var order = Ids.New();
            await Assert.ThrowsAsync<EventSchemaException>(() => scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(ProbeModule.Placed, order, new OrderPlaced(order, "free text with spaces", 1, Ids.New()), Cancel));
            await transaction.CommitAsync(Cancel);
        }

        Assert.Equal(0, await EventsTenant.OutboxCountAsync(provider));
    }

    [Fact]
    [Proves("TF-085 AC1")]
    public async Task ACommittedEventIsHandledWithinTenSecondsOfTheWorkerRunning()
    {
        var tenant = await fixture.NewTenantAsync();
        await using var provider = tenant.Build(TimeProvider.System);
        // The unit's job, as the worker runs it: one run, passing every 200 ms.
        var job = new EventDispatchJob(provider.GetRequiredService<EventDispatcher>(), provider.GetRequiredService<DispatchOptions>(), TimeProvider.System);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var running = job.RunAsync(new JobContext(Ids.New(), EventDispatchJob.Definition.Name, "test", 1, DateTimeOffset.UtcNow, null, null), stop.Token);
        try
        {
            var latencies = new List<TimeSpan>();
            for (var i = 0; i < 20; i++)
            {
                var order = Ids.New();
                var watch = Stopwatch.StartNew();
                var id = await EventsTenant.PublishAsync(provider, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", i, Ids.New()));
                while (true)
                {
                    var inbox = await EventsTenant.InboxAsync(provider, id);
                    if (inbox.Count == 3 && inbox.All(entry => entry.Status == DeliveryStates.Handled))
                    {
                        break;
                    }

                    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "The event was not handled within ten seconds of its commit (TF-085 AC1).");
                    await Task.Delay(20, Cancel);
                }

                latencies.Add(watch.Elapsed);
            }

            latencies.Sort();
            TestContext.Current.TestOutputHelper?.WriteLine($"From commit to every subscriber handled: median {latencies[latencies.Count / 2].TotalMilliseconds:F0} ms, longest {latencies[^1].TotalMilliseconds:F0} ms, over {latencies.Count} events.");
        }
        finally
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    [Fact]
    [Proves("ADR-0060")]
    public async Task AnEventCommittedBeforeACrashIsDeliveredAfterTheRestart()
    {
        var tenant = await fixture.NewTenantAsync();
        var order = Ids.New();
        Guid id;
        await using (var before = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow)))
        {
            id = await EventsTenant.PublishAsync(before, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()));

            // The worker stops here, before any pass: nothing in memory carries the event over.
        }

        await using var after = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        await EventsTenant.DrainAsync(after);

        Assert.All(await EventsTenant.InboxAsync(after, id), entry => Assert.Equal(DeliveryStates.Handled, entry.Status));
        Assert.Equal(1, await tenant.AuditCountAsync(ProbeModule.Counted, order));
    }
}
