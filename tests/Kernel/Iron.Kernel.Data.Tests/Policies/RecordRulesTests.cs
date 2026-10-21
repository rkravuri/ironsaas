using System.Linq.Expressions;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Policies;
using Iron.Kernel.Data.Tests.Sample;

namespace Iron.Kernel.Data.Tests.Policies;

/// <summary>
/// Stands in for Iron.Kernel.Authorization: what each permission of each principal reaches (ADR-0030). A permission not
/// granted reaches nothing.
/// </summary>
public sealed class TestRecordAccess : IRecordAccess
{
    private readonly Dictionary<(Guid, string), RecordAccess> _reach = [];

    public TestRecordAccess Grant(Guid principal, Permission permission, RecordAccess reach)
    {
        _reach[(principal, permission.Name)] = reach;
        return this;
    }

    public Task<RecordAccess> AccessibleAsync(Guid principalId, Permission permission, string recordType, CancellationToken cancellationToken) =>
        Task.FromResult(_reach.GetValueOrDefault((principalId, permission.Name), RecordAccess.Nothing));

    public PolicySubject Subject(Guid principal) => new(principal, (permission, ct) => AccessibleAsync(principal, permission, "test", ct));
}

/// <summary>Each building block on its own, without a database (TF-035: policies are unit-tested).</summary>
public class RecordRulesTests
{
    private static readonly Guid UnitA = Ids.New();
    private static readonly Guid UnitB = Ids.New();
    private static readonly Guid Reader = Ids.New();

    [Fact]
    [Proves("ADR-0032")]
    public async Task UnitVisibilityReadsTheUnitsAndRecordsTheReadPermissionReaches()
    {
        var inA = new Widget(UnitA, "in a");
        var inB = new Widget(UnitB, "in b");
        var grantedInB = new Widget(UnitB, "granted in b");
        var subject = new TestRecordAccess().Grant(Reader, SamplePermissions.WidgetsRead, RecordAccess.Within([UnitA], [grantedInB.Id])).Subject(Reader);
        var policy = RecordRules.UnitVisibility<Widget>(SamplePermissions.WidgetsRead);

        var readable = Matching(await policy.ReadableAsync(subject, TestContext.Current.CancellationToken), inA, inB, grantedInB);

        Assert.Equal(["in a", "granted in b"], readable.Select(w => w.Name));
        Assert.Equal(PolicyDecision.Allowed, await policy.DecideAsync(inA, RecordActions.Read, subject, TestContext.Current.CancellationToken));
        Assert.Equal(PolicyDecision.NotVisible, await policy.DecideAsync(inB, RecordActions.Read, subject, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0032")]
    public async Task UnitVisibilityAllowsAnActionWhereItsPermissionReachesAndHasNoRuleForOthers()
    {
        var inA = new Widget(UnitA, "in a");
        var inB = new Widget(UnitB, "in b");
        var subject = new TestRecordAccess()
            .Grant(Reader, SamplePermissions.WidgetsRead, RecordAccess.Within([UnitA, UnitB], []))
            .Grant(Reader, SamplePermissions.WidgetsManage, RecordAccess.Within([UnitA], []))
            .Subject(Reader);
        var policy = RecordRules.UnitVisibility<Widget>(SamplePermissions.WidgetsRead, (RecordActions.Update, SamplePermissions.WidgetsManage));

        Assert.Equal(PolicyDecision.Allowed, await policy.DecideAsync(inA, RecordActions.Update, subject, TestContext.Current.CancellationToken));
        Assert.Equal(PolicyDecision.NotAllowed, await policy.DecideAsync(inB, RecordActions.Update, subject, TestContext.Current.CancellationToken));
        Assert.Equal(PolicyDecision.NoRule, await policy.DecideAsync(inA, RecordActions.Delete, subject, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0032")]
    public async Task OwnerWhileLetsTheCreatorActWhileTheConditionHoldsAndDoesNotNarrowReading()
    {
        var mine = Created(new Proposal(UnitA, "mine"), Reader);
        var submitted = Created(new Proposal(UnitA, "submitted"), Reader);
        submitted.Submit();
        var theirs = Created(new Proposal(UnitA, "theirs"), Ids.New());
        var subject = new TestRecordAccess().Subject(Reader);
        var policy = RecordRules.OwnerWhile<Proposal>(p => p.Status == ProposalStatus.Draft, RecordActions.Update);

        Assert.Equal(3, Matching(await policy.ReadableAsync(subject, TestContext.Current.CancellationToken), mine, submitted, theirs).Count);
        Assert.Equal(PolicyDecision.Allowed, await policy.DecideAsync(mine, RecordActions.Update, subject, TestContext.Current.CancellationToken));
        Assert.Equal(PolicyDecision.NotAllowed, await policy.DecideAsync(submitted, RecordActions.Update, subject, TestContext.Current.CancellationToken));
        Assert.Equal(PolicyDecision.NotAllowed, await policy.DecideAsync(theirs, RecordActions.Update, subject, TestContext.Current.CancellationToken));
        Assert.Equal(PolicyDecision.NoRule, await policy.DecideAsync(mine, RecordActions.Delete, subject, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0032")]
    public async Task AllReadsWhatEveryPolicyReadsAndTakesTheStrictestAnswer()
    {
        var inA = new Widget(UnitA, "anvil");
        var bolt = new Widget(UnitA, "bolt");
        var subject = new TestRecordAccess().Grant(Reader, SamplePermissions.WidgetsRead, RecordAccess.Within([UnitA], [])).Subject(Reader);
        var policy = RecordRules.All(
            RecordRules.UnitVisibility<Widget>(SamplePermissions.WidgetsRead),
            new FixedPolicy<Widget>(w => w.Name.StartsWith('a'), PolicyDecision.NotAllowed),
            new FixedPolicy<Widget>(_ => true, PolicyDecision.NoRule));

        Assert.Equal(["anvil"], Matching(await policy.ReadableAsync(subject, TestContext.Current.CancellationToken), inA, bolt).Select(w => w.Name));
        Assert.Equal(PolicyDecision.NotAllowed, await policy.DecideAsync(inA, RecordActions.Update, subject, TestContext.Current.CancellationToken));
        Assert.Equal(PolicyDecision.NoRule, await RecordRules.All<Widget>(new FixedPolicy<Widget>(_ => true, PolicyDecision.NoRule)).DecideAsync(inA, RecordActions.Update, subject, TestContext.Current.CancellationToken));
        Assert.Equal(PolicyDecision.NotVisible, await RecordRules.All(new FixedPolicy<Widget>(_ => true, PolicyDecision.Allowed), new FixedPolicy<Widget>(_ => true, PolicyDecision.NotVisible)).DecideAsync(inA, RecordActions.Update, subject, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0032")]
    public async Task AnyReadsWhatOnePolicyReadsAndTakesTheMostGenerousAnswerSkippingNoRule()
    {
        var anvil = new Widget(UnitA, "anvil");
        var bolt = new Widget(UnitA, "bolt");
        var crank = new Widget(UnitA, "crank");
        var subject = new TestRecordAccess().Subject(Reader);
        var policy = RecordRules.Any(
            new FixedPolicy<Widget>(w => w.Name == "anvil", PolicyDecision.NotAllowed),
            new FixedPolicy<Widget>(w => w.Name == "bolt", PolicyDecision.Allowed),
            new FixedPolicy<Widget>(_ => false, PolicyDecision.NoRule));

        Assert.Equal(["anvil", "bolt"], Matching(await policy.ReadableAsync(subject, TestContext.Current.CancellationToken), anvil, bolt, crank).Select(w => w.Name));
        Assert.Equal(PolicyDecision.Allowed, await policy.DecideAsync(anvil, RecordActions.Update, subject, TestContext.Current.CancellationToken));
        Assert.Equal(PolicyDecision.NotAllowed, await RecordRules.Any(new FixedPolicy<Widget>(_ => true, PolicyDecision.NoRule), new FixedPolicy<Widget>(_ => true, PolicyDecision.NotAllowed)).DecideAsync(anvil, RecordActions.Update, subject, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0032")]
    public void ABlockNeedsWhatItWorksWith()
    {
        Assert.Throws<ArgumentException>(() => RecordRules.OwnerWhile<Proposal>(p => true));
        Assert.Throws<ArgumentException>(() => RecordRules.All<Widget>());
        Assert.Throws<ArgumentException>(() => RecordRules.Any<Widget>());
    }

    private static List<T> Matching<T>(Expression<Func<T, bool>> readable, params T[] records) => records.AsQueryable().Where(readable).ToList();

    /// <summary>A record as the audit stamp would leave it after its creator's save.</summary>
    private static Proposal Created(Proposal proposal, Guid creator)
    {
        typeof(AuditedEntity).GetProperty(nameof(AuditedEntity.CreatedBy))!.SetValue(proposal, creator);
        return proposal;
    }
}

/// <summary>A policy with a fixed filter and a fixed answer, for the combinators.</summary>
internal sealed class FixedPolicy<T>(Expression<Func<T, bool>> readable, PolicyDecision decision) : IRecordPolicy<T>
    where T : BusinessRecord
{
    public Task<Expression<Func<T, bool>>> ReadableAsync(PolicySubject subject, CancellationToken cancellationToken) => Task.FromResult(readable);

    public Task<PolicyDecision> DecideAsync(T record, string action, PolicySubject subject, CancellationToken cancellationToken) => Task.FromResult(decision);
}
