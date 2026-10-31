using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Events.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Events.Tests;

/// <summary>
/// A suite's waiting deliveries when the suite is removed (ADR-0060, at T1's review): held, with no attempt, no alert and
/// no dead letter, and pending again by themselves when the suite is added back. A unit learns its suites when its process
/// starts, so each provider here is the unit after an update job.
/// </summary>
[Collection(EventsCollection.Name)]
public class SuiteHoldTests(EventsFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("ADR-0060", "ADR-0059")]
    public async Task ARemovedSuitesWaitingDeliveryIsHeldWithoutAnAlertAndResumesWhenTheSuiteReturns()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var order = Ids.New();
        Guid id;

        // The event is handed on to the suite's subscriber, then the unit is updated without the suite before it is delivered.
        tenant.RunsProbeSuite = true;
        await using (var withSuite = tenant.Build(time))
        {
            id = await EventsTenant.PublishAsync(withSuite, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()));
            Assert.Equal(1, await withSuite.GetRequiredService<EventDispatcher>().HandOnAsync(Cancel));
        }

        tenant.RunsProbeSuite = false;
        await using (var without = tenant.Build(time))
        {
            var first = await EventsTenant.PassAsync(without);
            Assert.Equal(1, first.Held);

            // Past every retry the dispatcher would have made: still held, and the Foundation's subscribers are not held up.
            time.Advance(TimeSpan.FromHours(2));
            await EventsTenant.DrainAsync(without);
            var inbox = await EventsTenant.InboxAsync(without, id);
            var held = inbox.Single(e => e.Subscriber == ProbeSuiteModule.Subscriber);
            Assert.Equal((DeliveryStates.Held, 0, (string?)null, (string?)ProbeSuiteModule.SuiteShortName), (held.Status, held.Attempts, held.LastError, held.Suite));
            Assert.NotNull(held.HeldAt);
            Assert.All(inbox.Where(e => e.Subscriber != ProbeSuiteModule.Subscriber), e => Assert.Equal(DeliveryStates.Handled, e.Status));
            Assert.Equal(0, await tenant.AuditCountAsync(EventsAuditEvents.DeliveryDeadLettered, id));
            Assert.Empty(tenant.Behaviour.SuiteCounted);

            // The event reads as held, and the archive leaves it past the retention, since its delivery is still owed.
            await using var scope = without.CreateAsyncScope();
            Assert.Equal(DeliveryStates.Held, (await scope.ServiceProvider.GetRequiredService<EventInspection>().GetAsync(id, Cancel)).Value.Status);
            time.Advance(TimeSpan.FromDays(91));
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<EventArchiver>().RunAsync(Cancel));
        }

        tenant.RunsProbeSuite = true;
        await using (var back = tenant.Build(time))
        {
            var passes = await EventsTenant.DrainAsync(back);
            Assert.Equal(1, passes[0].Resumed);
            var resumed = (await EventsTenant.InboxAsync(back, id)).Single(e => e.Subscriber == ProbeSuiteModule.Subscriber);
            Assert.Equal((DeliveryStates.Handled, 1), (resumed.Status, resumed.Attempts));
            Assert.Null(resumed.HeldAt);
            Assert.Equal([order], tenant.Behaviour.SuiteCounted);
        }
    }

    [Fact]
    [Proves("ADR-0060")]
    public async Task OnlyASuitesDeliveriesAreHeldAFoundationSubscriberTheUnitNoLongerHoldsIsADeadLetter()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var order = Ids.New();
        Guid id;
        await using (var before = tenant.Build(time))
        {
            id = await EventsTenant.PublishAsync(before, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()));
            Assert.Equal(1, await before.GetRequiredService<EventDispatcher>().HandOnAsync(Cancel));
        }

        // A Foundation module's subscription that disappears is a programmer's error, which the operator must see.
        tenant.RunsProbe = false;
        tenant.Configuration["Events:Dispatch:Attempts"] = "1";
        await using var after = tenant.Build(time);
        await EventsTenant.DrainAsync(after);

        var inbox = await EventsTenant.InboxAsync(after, id);
        Assert.Equal(3, inbox.Count);
        Assert.All(inbox, e =>
        {
            Assert.Equal((DeliveryStates.Dead, (string?)null), (e.Status, e.Suite));
            Assert.StartsWith("events.subscriber.unknown", e.LastError, StringComparison.Ordinal);
        });
        Assert.Equal(3, await tenant.AuditCountAsync(EventsAuditEvents.DeliveryDeadLettered, id));
    }
}
