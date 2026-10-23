using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Data.Retention;
using Iron.Kernel.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Audit.Tests;

/// <summary>
/// Legal holds and retention choices keep their field-level history in the trail (TF-062, ADR-0051, the T7 review): each
/// save is a chained record event with every field that moved, old and new, under the record type their History reads.
/// </summary>
[Collection(AuditCollection.Name)]
public class TrackedHistoryTests(AuditFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-062 AC1", "ADR-0051")]
    public async Task ALegalHoldPlacedAndReleasedHasItsCreationAndItsReleaseFieldByField()
    {
        var database = await fixture.NewChainDatabaseAsync("hold_history");
        var operatorId = Ids.New();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new FixedPrincipal(operatorId), database);
        Guid holdId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var holds = scope.ServiceProvider.GetRequiredService<LegalHolds>();
            var placed = await holds.PlaceAsync(LegalHoldScopes.Record, "sample_order", Ids.New(), "Disputed invoice", Cancel);
            holdId = placed.Value.Id;
            Assert.True((await holds.ReleaseAsync(holdId, "Dispute settled", Cancel)).IsSuccess);
        }

        var events = await HistoryAsync(provider, holdId);

        Assert.Equal([AuditEvents.RecordCreated, AuditEvents.RecordChanged], events.Select(e => e.EventType));
        Assert.All(events, e => Assert.Equal("legal_hold", e.RecordType));
        Assert.Contains(Fields(events[0]), f => f.Field == "reason" && f.New == "Disputed invoice");
        var released = Fields(events[1]);
        Assert.Equal(["release_reason", "released_at", "released_by_id"], released.Select(f => f.Field).Order(StringComparer.Ordinal));
        Assert.Equal((null, "Dispute settled"), (released.Single(f => f.Field == "release_reason").Old, released.Single(f => f.Field == "release_reason").New));
        Assert.All(events, e => Assert.Equal(operatorId, e.ActorId));
    }

    [Fact]
    [Proves("TF-062 AC1", "ADR-0051")]
    public async Task ARetentionChoiceChangedHasItsDaysOldAndNewAndItsIdForThePage()
    {
        var database = await fixture.NewChainDatabaseAsync("retention_history");
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new FixedPrincipal(Ids.New()), database);
        RetentionClassView first, second;
        await using (var scope = provider.CreateAsyncScope())
        {
            var policy = scope.ServiceProvider.GetRequiredService<RetentionPolicy>();
            first = (await policy.ChooseAsync(RetentionClasses.Session.Code, 200, "a policy review", Cancel)).Value;
            second = (await policy.ChooseAsync(RetentionClasses.Session.Code, 250, "the auditors asked", Cancel)).Value;
        }

        var events = await HistoryAsync(provider, first.ChoiceId!.Value);
        await using var listing = provider.CreateAsyncScope();
        var listed = (await listing.ServiceProvider.GetRequiredService<RetentionPolicy>().ClassesAsync(Cancel)).Single(c => c.Class == RetentionClasses.Session);

        Assert.Equal(first.ChoiceId, second.ChoiceId);
        Assert.Equal(first.ChoiceId, listed.ChoiceId);
        Assert.Equal([AuditEvents.RecordCreated, AuditEvents.RecordChanged], events.Select(e => e.EventType));
        Assert.All(events, e => Assert.Equal(RetentionChoice.RecordType, e.RecordType));
        var days = Assert.Single(Fields(events[1]));
        Assert.Equal(("days", "200", "250"), (days.Field, days.Old, days.New));
    }

    private static async Task<List<AuditEventRecord>> HistoryAsync(IServiceProvider provider, Guid recordId)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.RecordId == recordId)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .ToListAsync(Cancel);
    }

    private static List<FieldChange> Fields(AuditEventRecord record) =>
        JsonSerializer.Deserialize<List<FieldChange>>(record.Changes!, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
}
