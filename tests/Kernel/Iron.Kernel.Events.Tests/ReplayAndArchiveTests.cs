using System.Text;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Events.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Events.Tests;

/// <summary>Replay to a subscriber (TF-112 AC1), idempotent through the inbox, and the archive past the retention (TF-112 AC2).</summary>
[Collection(EventsCollection.Name)]
public class ReplayAndArchiveTests(EventsFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-112 AC1")]
    public async Task ADeadLetteredEventReplayedRunsTheHandlerAgainAndItsStatusUpdates()
    {
        var tenant = await fixture.NewTenantAsync();
        tenant.Behaviour.FlakyFailures = -1;
        tenant.Configuration["Events:Dispatch:Attempts"] = "1";
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var administrator = Ids.New();
        await using var provider = tenant.Build(time, administrator);
        var order = Ids.New();
        var id = await EventsTenant.PublishAsync(provider, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()));
        await EventsTenant.DrainAsync(provider);
        Assert.Equal(DeliveryStates.Dead, (await EventsTenant.InboxAsync(provider, id)).Single(e => e.Subscriber == ProbeModule.Flaky).Status);

        tenant.Behaviour.FlakyFailures = 0;
        ReplayResult replayed;
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<EventInspection>().ReplayAsync(new ReplayRequest(ProbeModule.Flaky, [id], null, null, "The probe is fixed (ticket 12)."), null, Cancel);
            Assert.True(result.IsSuccess, result.ToString());
            replayed = result.Value;
        }

        Assert.Equal(new ReplayResult(ProbeModule.Flaky, 1, 1, 0, 0), replayed);
        var pending = (await EventsTenant.InboxAsync(provider, id)).Single(e => e.Subscriber == ProbeModule.Flaky);
        Assert.Equal((DeliveryStates.Pending, 0, 1), (pending.Status, pending.Attempts, pending.Replays));

        await EventsTenant.DrainAsync(provider);
        var handled = (await EventsTenant.InboxAsync(provider, id)).Single(e => e.Subscriber == ProbeModule.Flaky);
        Assert.Equal(DeliveryStates.Handled, handled.Status);
        Assert.Equal(1, await tenant.AuditCountAsync(ProbeModule.FlakyHandled, order));
        Assert.Equal(1, await tenant.AuditCountAsync(EventsAuditEvents.ReplayRequested, id));
    }

    [Fact]
    [Proves("TF-112 AC1", "ADR-0060")]
    public async Task TheUnitsEventsCommandListsDeadLettersWithoutTheirDataAndReplaysThemInTheOperatorsName()
    {
        var tenant = await fixture.NewTenantAsync();
        tenant.Behaviour.FlakyFailures = -1;
        tenant.Configuration["Events:Dispatch:Attempts"] = "1";
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var order = Ids.New();
        Guid id;
        await using (var provider = tenant.Build(time))
        {
            id = await EventsTenant.PublishAsync(provider, ProbeModule.Placed, order, new OrderPlaced(order, "PO-SECRET-7", 1, Ids.New()));
            await EventsTenant.DrainAsync(provider);
        }

        // As the fleet's job runs it: what the operator asked is in the unit's configuration, the answer is the output.
        tenant.Configuration["Events:Command:Action"] = EventsCommand.Search;
        tenant.Configuration["Events:Command:Status"] = DeliveryStates.Dead;
        var (searched, found) = await CommandAsync(tenant, time);
        tenant.Configuration["Events:Command:Action"] = EventsCommand.Replay;
        tenant.Configuration["Events:Command:Subscriber"] = ProbeModule.Flaky;
        tenant.Configuration["Events:Command:Ids"] = "not-an-id";
        tenant.Configuration["Events:Command:Reason"] = "Ticket 12: the probe is fixed.";
        tenant.Configuration["Events:Command:Operator"] = "olga@control.test";
        var (refused, _) = await CommandAsync(tenant, time);
        tenant.Configuration["Events:Command:Ids"] = id.ToString("D");
        var (replayed, answer) = await CommandAsync(tenant, time);

        Assert.Equal((0, 1, 0), (searched, refused, replayed));
        Assert.Contains($"{id:D} {ProbeModule.Placed.Name} v1", found, StringComparison.Ordinal);
        Assert.Contains($"{ProbeModule.Flaky}=dead(1)", found, StringComparison.Ordinal);
        Assert.DoesNotContain("PO-SECRET-7", found, StringComparison.Ordinal);
        Assert.Contains("Searched: 1 event(s)", found, StringComparison.Ordinal);
        Assert.Contains("1 put back to pending", answer, StringComparison.Ordinal);
        await using var after = tenant.Build(time);
        Assert.Equal(DeliveryStates.Pending, (await EventsTenant.InboxAsync(after, id)).Single(e => e.Subscriber == ProbeModule.Flaky).Status);
        Assert.Equal(1, await tenant.AuditCountAsync(EventsAuditEvents.ReplayRequested, id));
        Assert.Equal("olga@control.test", await tenant.AuditOperatorAsync(EventsAuditEvents.ReplayRequested, id));
    }

    /// <summary>Runs the unit's <c>events</c> command with the tenant's configuration, and returns its exit code and output.</summary>
    private static async Task<(int Code, string Output)> CommandAsync(EventsTenant tenant, TimeProvider time)
    {
        await using var provider = tenant.Build(time);
        var writer = new StringWriter();
        var original = Console.Out;
        Console.SetOut(writer);
        try
        {
            var code = await Iron.Kernel.Data.Commands.DatabaseCommands.RunAsync(provider, [Iron.Kernel.Data.Commands.DatabaseCommands.Events], Cancel);
            return (code, writer.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    [Proves("ADR-0060")]
    public async Task AReplayOfHandledEventsChangesNothingAndAReplayNeedsAReasonAndAKnownSubscriber()
    {
        var tenant = await fixture.NewTenantAsync();
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var orders = Enumerable.Range(0, 3).Select(_ => Ids.New()).ToList();
        foreach (var order in orders)
        {
            await EventsTenant.PublishAsync(provider, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()));
        }

        await EventsTenant.DrainAsync(provider);
        await using var scope = provider.CreateAsyncScope();
        var inspection = scope.ServiceProvider.GetRequiredService<EventInspection>();

        var range = await inspection.ReplayAsync(new ReplayRequest(ProbeModule.Counter, null, 1, null, "Check the counter."), "Platform operator", Cancel);
        Assert.Equal(new ReplayResult(ProbeModule.Counter, 3, 0, 3, 0), range.Value);
        Assert.False((await EventsTenant.PassAsync(provider)).DidWork);
        foreach (var order in orders)
        {
            Assert.Equal(1, await tenant.AuditCountAsync(ProbeModule.Counted, order));
        }

        Assert.Equal(EventsErrors.ReplayReasonRequired, (await inspection.ReplayAsync(new ReplayRequest(ProbeModule.Counter, null, 1, null, "  "), null, Cancel)).Error.Code);
        Assert.Equal(EventsErrors.SubscriberUnknown, (await inspection.ReplayAsync(new ReplayRequest("probe.nobody", null, 1, null, "Why not."), null, Cancel)).Error.Code);
        Assert.Equal(EventsErrors.ReplayNothing, (await inspection.ReplayAsync(new ReplayRequest(ProbeModule.Counter, [Ids.New()], null, null, "Why not."), null, Cancel)).Error.Code);
    }

    [Fact]
    [Proves("TF-112 AC2")]
    public async Task AnEventOlderThanTheRetentionIsArchivedAndReportedAsArchived()
    {
        var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = tenant.Build(time);
        var order = Ids.New();
        var id = await EventsTenant.PublishAsync(provider, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()));
        await EventsTenant.DrainAsync(provider);

        // Within the retention it stays.
        time.Advance(TimeSpan.FromDays(89));
        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<EventArchiver>().RunAsync(Cancel));
        }

        time.Advance(TimeSpan.FromDays(2));
        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<EventArchiver>().RunAsync(Cancel));
        }

        Assert.Equal(0, await EventsTenant.OutboxCountAsync(provider));
        var archive = Assert.Single(tenant.Storage.Objects);
        Assert.StartsWith(EventArchiver.Prefix, archive.Key, StringComparison.Ordinal);
        var line = Encoding.UTF8.GetString(archive.Value);
        Assert.Contains($"\"id\":\"{id}\"", line, StringComparison.Ordinal);
        Assert.Contains($"\"subscriber\":\"{ProbeModule.Counter}\"", line, StringComparison.Ordinal);

        await using var read = provider.CreateAsyncScope();
        var inspection = read.ServiceProvider.GetRequiredService<EventInspection>();
        Assert.Equal(EventsErrors.EventArchived, (await inspection.GetAsync(id, Cancel)).Error.Code);
        Assert.Equal(EventsErrors.EventNotFound, (await inspection.GetAsync(Ids.New(time), Cancel)).Error.Code);
        var retention = await inspection.RetentionAsync(Cancel);
        Assert.Equal((90, 1), (retention.Days, retention.Archives));
    }

    [Fact]
    [Proves("ADR-0060")]
    public async Task ADeadLetterIsKeptPastTheRetention()
    {
        var tenant = await fixture.NewTenantAsync();
        tenant.Behaviour.FlakyFailures = -1;
        tenant.Configuration["Events:Dispatch:Attempts"] = "1";
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = tenant.Build(time);
        var order = Ids.New();
        var id = await EventsTenant.PublishAsync(provider, ProbeModule.Placed, order, new OrderPlaced(order, "PO-1", 1, Ids.New()));
        await EventsTenant.DrainAsync(provider);

        time.Advance(TimeSpan.FromDays(400));
        await using var scope = provider.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<EventArchiver>().RunAsync(Cancel));
        Assert.True((await scope.ServiceProvider.GetRequiredService<EventInspection>().GetAsync(id, Cancel)).IsSuccess);
    }
}
