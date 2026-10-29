using Iron.Kernel.Audit.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Audit.Tests;

/// <summary>
/// What the security alert rules count (ADR-0056): audit events in a window, by group, with the conditions a rule may
/// set. Each test writes event types of its own, so the shared database's other events never count.
/// </summary>
[Collection(AuditCollection.Name)]
public class AuditCountTests(AuditFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("ADR-0056")]
    public async Task EventsAreCountedByPrincipalWithinTheWindowAndNeverForTheAnonymousActor()
    {
        var (time, start) = Clock();
        var type = Type();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await WriteAsync(time, start, new AuditEvent(type, AuditOutcome.Denied, first));
        await WriteAsync(time, start.AddMinutes(1), new AuditEvent(type, AuditOutcome.Denied, first));
        await WriteAsync(time, start.AddMinutes(2), new AuditEvent(type, AuditOutcome.Denied, first));
        await WriteAsync(time, start.AddMinutes(3), new AuditEvent(type, AuditOutcome.Denied, second));
        await WriteAsync(time, start.AddMinutes(4), new AuditEvent(type, AuditOutcome.Denied, Guid.Empty));
        await WriteAsync(time, start.AddMinutes(20), new AuditEvent(type, AuditOutcome.Denied, first));

        var counts = await CountAsync(new AuditCountQuery([new AuditEventMatch(type)], AuditGrouping.Principal, start.AddSeconds(-1), start.AddMinutes(10)));

        Assert.Equal(2, counts.Count);
        var mine = Assert.Single(counts, c => c.Group == first.ToString());
        Assert.Equal(3, mine.Events);
        Assert.Equal(3, mine.Measure);
        Assert.Equal(start, mine.First);
        Assert.Equal(start.AddMinutes(2), mine.Last);
        Assert.Equal(1, Assert.Single(counts, c => c.Group == second.ToString()).Events);
    }

    [Fact]
    [Proves("ADR-0056")]
    public async Task EventsAreGroupedByAddressTargetRecordOrAllAndAWindowsStartIsExclusive()
    {
        var (time, start) = Clock();
        var type = Type();
        var record = Guid.NewGuid();
        await WriteAsync(time, start, new AuditEvent(type, AuditOutcome.Denied, Guid.Empty, "ana@acme.test", Address("203.0.113.7")));
        await WriteAsync(time, start.AddMinutes(1), new AuditEvent(type, AuditOutcome.Denied, Guid.Empty, "ana@acme.test", Address("203.0.113.7"), RecordType: "incident", RecordId: record));
        await WriteAsync(time, start.AddMinutes(2), new AuditEvent(type, AuditOutcome.Denied, Guid.Empty, "ben@acme.test", Address("198.51.100.4")));

        var window = (After: start.AddSeconds(-1), Until: start.AddMinutes(5));
        var byAddress = await CountAsync(new AuditCountQuery([new AuditEventMatch(type)], AuditGrouping.Address, window.After, window.Until));
        var byTarget = await CountAsync(new AuditCountQuery([new AuditEventMatch(type)], AuditGrouping.Target, window.After, window.Until));
        var byRecord = await CountAsync(new AuditCountQuery([new AuditEventMatch(type)], AuditGrouping.Record, window.After, window.Until));
        var everything = await CountAsync(new AuditCountQuery([new AuditEventMatch(type)], AuditGrouping.Everything, window.After, window.Until));
        var afterTheFirst = await CountAsync(new AuditCountQuery([new AuditEventMatch(type)], AuditGrouping.Everything, start, window.Until));

        Assert.Equal(2, Assert.Single(byAddress, c => c.Group == "203.0.113.7").Events);
        Assert.Equal(1, Assert.Single(byAddress, c => c.Group == "198.51.100.4").Events);
        Assert.Equal(2, Assert.Single(byTarget, c => c.Group == "ana@acme.test").Events);
        Assert.Equal(record.ToString(), Assert.Single(byRecord).Group);
        Assert.Equal(3, Assert.Single(everything).Events);
        Assert.Equal(2, Assert.Single(afterTheFirst).Events);
    }

    [Fact]
    [Proves("TF-079 AC3", "ADR-0056")]
    public async Task OnlyAnExportAboveTheLimitCountsAndASumAddsTheReportsASummaryStandsFor()
    {
        var (time, start) = Clock();
        var exported = Type();
        var summarised = Type();
        var actor = Guid.NewGuid();
        await WriteAsync(time, start, new AuditEvent(exported, AuditOutcome.Success, actor, Details: Rows("10000")));
        await WriteAsync(time, start.AddSeconds(1), new AuditEvent(exported, AuditOutcome.Success, actor, Details: Rows("10001")));
        await WriteAsync(time, start.AddSeconds(2), new AuditEvent(exported, AuditOutcome.Success, actor, Details: Rows("many")));
        await WriteAsync(time, start.AddSeconds(3), new AuditEvent(exported, AuditOutcome.Success, actor, Details: Rows("99999999999999999999")));
        await WriteAsync(time, start.AddSeconds(4), new AuditEvent(summarised, AuditOutcome.Success, actor, Details: Reports("40")));
        await WriteAsync(time, start.AddSeconds(5), new AuditEvent(summarised, AuditOutcome.Success, actor, Details: Reports("70")));

        var large = await CountAsync(new AuditCountQuery([new AuditEventMatch(exported)], AuditGrouping.Principal, start.AddSeconds(-1), start.AddMinutes(1), QuantityKey: AuditDetails.Rows, Above: 10_000));
        var sum = await CountAsync(new AuditCountQuery([new AuditEventMatch(summarised)], AuditGrouping.Everything, start.AddSeconds(-1), start.AddMinutes(1), SumKey: AuditDetails.Reports));

        // Ten thousand is not more than ten thousand; a value that is no whole number, or too long to be one, never counts.
        Assert.Equal(1, Assert.Single(large).Events);
        Assert.Equal(2, Assert.Single(sum).Events);
        Assert.Equal(110, Assert.Single(sum).Measure);
    }

    [Fact]
    [Proves("ADR-0056")]
    public async Task AMatchNarrowsByOutcomeDetailAndTargetAndTheMatchesOfACountAddUpByGroup()
    {
        var (time, start) = Clock();
        var role = Type();
        var setting = Type();
        var actor = Guid.NewGuid();
        await WriteAsync(time, start, new AuditEvent(role, AuditOutcome.Success, actor, "approver", Critical()));
        await WriteAsync(time, start.AddSeconds(1), new AuditEvent(role, AuditOutcome.Success, actor, "reader"));
        await WriteAsync(time, start.AddSeconds(2), new AuditEvent(role, AuditOutcome.Denied, actor, "approver", Critical()));
        await WriteAsync(time, start.AddSeconds(3), new AuditEvent(setting, AuditOutcome.Success, actor, "identity.mfa.required_for"));
        await WriteAsync(time, start.AddSeconds(4), new AuditEvent(setting, AuditOutcome.Success, actor, "shell.time_zone"));

        AuditEventMatch[] matches =
        [
            new(role, Outcome: AuditOutcome.Success, DetailKey: AuditDetails.Critical, DetailValue: AuditDetails.True),
            new(setting, TargetPrefix: "identity.mfa."),
        ];
        var counts = await CountAsync(new AuditCountQuery(matches, AuditGrouping.Principal, start.AddSeconds(-1), start.AddMinutes(1)));
        var one = await CountAsync(new AuditCountQuery(matches, AuditGrouping.Principal, start.AddSeconds(-1), start.AddMinutes(1), Group: Guid.NewGuid().ToString()));

        var group = Assert.Single(counts);
        Assert.Equal(2, group.Events);
        Assert.Equal(start, group.First);
        Assert.Equal(start.AddSeconds(3), group.Last);
        Assert.Empty(one);
    }

    /// <summary>A clock of the test's own from now, in whole seconds so a time reads back as it was written.</summary>
    private static (FakeTimeProvider Time, DateTimeOffset Start) Clock()
    {
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
        return (new FakeTimeProvider(start), start);
    }

    private static string Type() => $"test.count_{Guid.NewGuid():N}";

    private static Dictionary<string, string> Address(string address) => new(StringComparer.Ordinal) { [AuditDetails.Address] = address };

    private static Dictionary<string, string> Rows(string rows) => new(StringComparer.Ordinal) { [AuditDetails.Rows] = rows };

    private static Dictionary<string, string> Reports(string reports) => new(StringComparer.Ordinal) { [AuditDetails.Reports] = reports };

    private static Dictionary<string, string> Critical() => new(StringComparer.Ordinal) { [AuditDetails.Critical] = AuditDetails.True };

    private async Task WriteAsync(FakeTimeProvider time, DateTimeOffset at, AuditEvent auditEvent)
    {
        time.SetUtcNow(at);
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(SystemPrincipal.SystemId));
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(auditEvent, Cancel);
    }

    private async Task<IReadOnlyList<AuditGroupCount>> CountAsync(AuditCountQuery query)
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAuditCounts>().CountAsync(query, Cancel);
    }
}
