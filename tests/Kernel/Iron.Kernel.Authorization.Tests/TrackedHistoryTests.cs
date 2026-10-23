using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.History;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>
/// Delegations and conflict rules keep their field-level history (TF-062, ADR-0051, the T7 review): each save is one
/// change with every field that moved, old and new, under the record type their own events use, so a record's History
/// shows both.
/// </summary>
[Collection(AuthorizationCollection.Name)]
public class TrackedHistoryTests(AuthorizationFixture fixture)
{
    private static readonly DateTimeOffset Now = new FakeTimeProvider().GetUtcNow();

    [Fact]
    [Proves("TF-062 AC1", "ADR-0051")]
    public async Task ADelegationEndedEarlyRecordsTheFieldsItsEndChanged()
    {
        var (ada, grace) = (fixture.Principals.Add(), fixture.Principals.Add());
        var history = new RecordingChangeRecorder();
        await using var scope = fixture.HistoryScope(ada, history);
        var service = scope.Get<DelegationService>();

        var created = await service.CreateAsync(new DelegationInput(null, grace, [DutyProbeModule.Approval.Code], Now.AddMinutes(-5), Now.AddDays(5), "Annual leave."), "test", TestContext.Current.CancellationToken);
        var ended = await service.EndAsync(created.Value.Id, "Back early.", "test", TestContext.Current.CancellationToken);

        Assert.True(ended.IsSuccess, ended.IsFailure ? ended.Error.Message : null);
        var made = Assert.Single(history.Changes, c => c.RecordId == created.Value.Id && c.Kind == RecordChangeKinds.Created);
        Assert.Equal(Delegation.RecordType, made.RecordType);
        Assert.Contains(made.Fields, f => f.Field == "reason" && f.New == "Annual leave.");
        var change = Assert.Single(history.Changes, c => c.RecordId == created.Value.Id && c.Kind == RecordChangeKinds.Changed);
        Assert.Equal((Delegation.RecordType, 2), (change.RecordType, change.Version));
        Assert.Equal(["end_reason", "ended_at", "ended_by"], change.Fields.Select(f => f.Field).Order(StringComparer.Ordinal));
        Assert.Equal((null, "Back early."), (change.Fields.Single(f => f.Field == "end_reason").Old, change.Fields.Single(f => f.Field == "end_reason").New));
        Assert.Equal(ada.ToString("D"), change.Fields.Single(f => f.Field == "ended_by").New);
    }

    [Fact]
    [Proves("TF-062 AC1", "ADR-0051")]
    public async Task AConflictRuleChangedRecordsEachFieldThatMovedOldAndNew()
    {
        var database = await fixture.CreateTenantDatabaseAsync($"tracked_{Ids.New():N}");
        var owner = fixture.Principals.Add();
        var history = new RecordingChangeRecorder();
        await using var scope = fixture.HistoryScope(owner, history, database);
        var rules = scope.Get<ConflictService>();
        var first = new ConflictSide(ConflictSides.Permission, "authz.units.manage");
        var second = new ConflictSide(ConflictSides.Permission, "authz.roles.manage");

        var created = await rules.CreateAsync(new ConflictRuleInput("Two hands", first, second, ConflictModes.Warn, "Separate hands.", owner), TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
        var changed = await rules.UpdateAsync(created.Value.Id, new ConflictRuleInput("Two hands, strictly", first, second, ConflictModes.Block, "Separate hands, always.", owner), TestContext.Current.CancellationToken);

        Assert.True(changed.IsSuccess, changed.IsFailure ? changed.Error.Message : null);
        var change = Assert.Single(history.Changes, c => c.RecordId == created.Value.Id && c.Kind == RecordChangeKinds.Changed);
        Assert.Equal(ConflictRule.RecordType, change.RecordType);
        Assert.Equal(["mode", "name", "reason"], change.Fields.Select(f => f.Field).Order(StringComparer.Ordinal));
        Assert.Equal(("warn", "block"), (change.Fields.Single(f => f.Field == "mode").Old, change.Fields.Single(f => f.Field == "mode").New));
        Assert.Equal(("Two hands", "Two hands, strictly"), (change.Fields.Single(f => f.Field == "name").Old, change.Fields.Single(f => f.Field == "name").New));
    }
}
