using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Events.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Events.Tests;

/// <summary>The dispatcher (TF-085, TF-111, ADR-0060): at least once, once per subscriber, in order per record, retried, dead-lettered.</summary>
[Collection(EventsCollection.Name)]
public class DispatchTests(EventsFixture fixture)
{
    [Fact]
    [Proves("TF-085 AC3", "TF-111 AC2")]
    public async Task AnEventDeliveredTwiceHasNoDuplicateEffect()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var first = tenant.Build(time);
        await using var second = tenant.Build(time);
        var order = Ids.New();
        var id = await EventsTenant.PublishAsync(first, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()));
        Assert.Equal(1, await first.GetRequiredService<EventDispatcher>().HandOnAsync(TestContext.Current.CancellationToken));
        var counter = (await EventsTenant.InboxAsync(first, id)).Single(e => e.Subscriber == ProbeModule.Counter);

        // Two workers deliver the same entry at once, as an overlap during a restart would: one runs the handler.
        var outcomes = await Task.WhenAll(
            first.GetRequiredService<EventDispatcher>().DeliverAsync(counter.Id, TestContext.Current.CancellationToken),
            second.GetRequiredService<EventDispatcher>().DeliverAsync(counter.Id, TestContext.Current.CancellationToken));
        Assert.Equal([DeliveryOutcome.Handled, DeliveryOutcome.AlreadyHandled], outcomes.Order());

        // And again later, as a delivery whose acknowledgement was lost would be: the inbox says handled, nothing runs.
        Assert.Equal(DeliveryOutcome.AlreadyHandled, await second.GetRequiredService<EventDispatcher>().DeliverAsync(counter.Id, TestContext.Current.CancellationToken));
        await EventsTenant.DrainAsync(first);

        Assert.Equal(1, await tenant.AuditCountAsync(ProbeModule.Counted, order));
        Assert.Equal((DeliveryStates.Handled, 1), (await EventsTenant.InboxAsync(first, id)).Single(e => e.Subscriber == ProbeModule.Counter) is var entry ? (entry.Status, entry.Attempts) : default);
    }

    [Fact]
    [Proves("ADR-0060")]
    public async Task EventsAboutOneRecordReachEachSubscriberInTheOrderTheyWereWrittenWhileRecordsRunInParallel()
    {
        var tenant = await fixture.NewTenantAsync();
        tenant.Behaviour.SequenceDelayMilliseconds = 15;
        tenant.Configuration["Events:Dispatch:Parallelism"] = "4";
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var records = Enumerable.Range(0, 5).Select(_ => Ids.New()).ToList();
        for (var sequence = 1; sequence <= 4; sequence++)
        {
            foreach (var record in records)
            {
                await EventsTenant.PublishAsync(provider, ProbeModule.Placed, record, new OrderPlaced(record, "PO-1", sequence, Ids.New()));
            }
        }

        await EventsTenant.DrainAsync(provider);

        var seen = tenant.Behaviour.Sequence.ToList();
        Assert.Equal(20, seen.Count);
        foreach (var record in records)
        {
            Assert.Equal([1, 2, 3, 4], seen.Where(s => s.Record == record).Select(s => s.Sequence));
        }

        // A pass delivered the first event of every record at once, not one record's events first.
        Assert.Equal(5, seen.Take(5).Select(s => s.Record).Distinct().Count());
    }

    [Fact]
    [Proves("TF-085 AC2", "TF-111 AC1")]
    public async Task AFailingSubscriberIsRetriedWithGrowingWaitsThenDeadLetteredWhileTheOthersComplete()
    {
        var tenant = await fixture.NewTenantAsync();
        tenant.Behaviour.FlakyFailures = -1;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = tenant.Build(time);
        var order = Ids.New();
        var id = await EventsTenant.PublishAsync(provider, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()));

        await EventsTenant.PassAsync(provider);
        var after = await EventsTenant.InboxAsync(provider, id);
        Assert.Equal(DeliveryStates.Handled, after.Single(e => e.Subscriber == ProbeModule.Counter).Status);
        Assert.Equal(DeliveryStates.Handled, after.Single(e => e.Subscriber == ProbeModule.Sequencer).Status);
        var flaky = after.Single(e => e.Subscriber == ProbeModule.Flaky);
        Assert.Equal((DeliveryStates.Pending, 1), (flaky.Status, flaky.Attempts));
        Assert.Equal(time.GetUtcNow().AddSeconds(2), flaky.NextAttemptAt, TimeSpan.FromMilliseconds(1));
        Assert.StartsWith("probe.flaky.failed:", flaky.LastError, StringComparison.Ordinal);

        // Not due yet: nothing runs. Each wait doubles: 2, 4, 8, 16, 32 seconds.
        Assert.Equal(0, (await EventsTenant.PassAsync(provider)).Failed);
        foreach (var wait in new[] { 2, 4, 8, 16 })
        {
            time.Advance(TimeSpan.FromSeconds(wait));
            Assert.Equal(1, (await EventsTenant.PassAsync(provider)).Failed);
        }

        time.Advance(TimeSpan.FromSeconds(32));
        Assert.Equal(1, (await EventsTenant.PassAsync(provider)).DeadLettered);

        flaky = (await EventsTenant.InboxAsync(provider, id)).Single(e => e.Subscriber == ProbeModule.Flaky);
        Assert.Equal((DeliveryStates.Dead, 6), (flaky.Status, flaky.Attempts));
        Assert.NotNull(flaky.DeadAt);
        Assert.Equal(6, tenant.Behaviour.FlakyAttempts);

        // Every failed attempt's work was rolled back; the other subscribers handled the event once; the dead letter is audited.
        Assert.Equal(0, await tenant.AuditCountAsync(ProbeModule.FlakyTried, order));
        Assert.Equal(1, await tenant.AuditCountAsync(ProbeModule.Counted, order));
        Assert.Equal(1, await tenant.AuditCountAsync(EventsAuditEvents.DeliveryDeadLettered, id));
        time.Advance(TimeSpan.FromHours(1));
        Assert.False((await EventsTenant.PassAsync(provider)).DidWork);
    }

    [Fact]
    [Proves("TF-085 AC2")]
    public async Task ASubscriberThatSucceedsOnARetryHandlesTheEventOnce()
    {
        var tenant = await fixture.NewTenantAsync();
        tenant.Behaviour.FlakyFailures = 2;
        tenant.Behaviour.Throws = true;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = tenant.Build(time);
        var order = Ids.New();
        var id = await EventsTenant.PublishAsync(provider, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()));

        await EventsTenant.PassAsync(provider);
        time.Advance(TimeSpan.FromSeconds(2));
        await EventsTenant.PassAsync(provider);
        time.Advance(TimeSpan.FromSeconds(4));
        await EventsTenant.PassAsync(provider);

        var flaky = (await EventsTenant.InboxAsync(provider, id)).Single(e => e.Subscriber == ProbeModule.Flaky);
        Assert.Equal((DeliveryStates.Handled, 3), (flaky.Status, flaky.Attempts));
        Assert.StartsWith("InvalidOperationException:", flaky.LastError, StringComparison.Ordinal);
        Assert.Equal(1, await tenant.AuditCountAsync(ProbeModule.FlakyHandled, order));
        Assert.Equal(1, await tenant.AuditCountAsync(ProbeModule.FlakyTried, order));
    }

    [Fact]
    [Proves("ADR-0060")]
    public async Task ADeadLetterHoldsBackNoLaterEventOfItsRecord()
    {
        var tenant = await fixture.NewTenantAsync();
        tenant.Behaviour.FlakyFailures = -1;
        tenant.Configuration["Events:Dispatch:Attempts"] = "1";
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = tenant.Build(time);
        var order = Ids.New();
        var first = await EventsTenant.PublishAsync(provider, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()));
        var second = await EventsTenant.PublishAsync(provider, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 2, Ids.New()));

        await EventsTenant.PassAsync(provider);
        Assert.Equal(DeliveryStates.Dead, (await EventsTenant.InboxAsync(provider, first)).Single(e => e.Subscriber == ProbeModule.Flaky).Status);
        Assert.Equal(DeliveryStates.Pending, (await EventsTenant.InboxAsync(provider, second)).Single(e => e.Subscriber == ProbeModule.Flaky).Status);

        // While the first was pending the second waited for it; dead, it holds nothing back.
        tenant.Behaviour.FlakyFailures = 0;
        await EventsTenant.PassAsync(provider);
        Assert.Equal(DeliveryStates.Handled, (await EventsTenant.InboxAsync(provider, second)).Single(e => e.Subscriber == ProbeModule.Flaky).Status);
    }
}
