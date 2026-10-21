using Iron.Kernel.Data.Lists;
using Iron.Kernel.Data.Policies;
using Iron.Kernel.Data.Tests.Sample;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Data.Tests.Policies;

/// <summary>
/// The sample module's policies against PostgreSQL (TF-035, ADR-0032): the scope rule first, the policy joined to it in
/// the query, and reads by id that answer not found or forbidden. Every test owns its units, so the records of other
/// tests stay in the tables, outside the reader's scope.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RecordPolicyTests(PostgresFixture fixture)
{
    private readonly Guid _unitA = Ids.New();
    private readonly Guid _unitB = Ids.New();
    private readonly Guid _reader = Ids.New();
    private readonly Guid _creator = Ids.New();

    [Fact]
    [Proves("TF-035 AC2")]
    public async Task AListsPageAndTotalCountOnlyTheRecordsThePolicyLetsTheReaderSee()
    {
        await SeedAsync(_creator, [.. Enumerable.Range(0, 7).Select(i => new Proposal(_unitA, $"a-{i}")), .. Enumerable.Range(0, 5).Select(i => new Proposal(_unitB, $"b-{i}"))]);
        var access = new TestRecordAccess().Grant(_reader, SamplePermissions.ProposalsRead, RecordAccess.Within([_unitA], []));
        await using var provider = Services(_reader, access, s => s.AddProposalPolicy());
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var fields = new ListFields<Proposal>().Add("title", p => p.Title).SortByDefault("title");
        var query = new ListQuery(new PageRequest(1, 3), [], [], []);

        var policed = await scope.ServiceProvider.GetRequiredService<IRecordPolicies>().ApplyAsync(context.Proposals.AsNoTracking(), TestContext.Current.CancellationToken);
        var page = await policed.ApplyListQuery(query, fields).ToPageAsync(query.Page, TestContext.Current.CancellationToken);

        Assert.Equal(7, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(["a-0", "a-1", "a-2"], page.Items.Select(p => p.Title));
        Assert.True(await context.Proposals.CountAsync(p => p.OwningOrganisationId == _unitB, TestContext.Current.CancellationToken) == 5, "the other unit's records are in the table");
    }

    [Fact]
    [Proves("TF-035 AC1", "ADR-0032")]
    public async Task AnotherUnitsRecordByIdIsNotFoundTheSameAsAnIdThatDoesNotExist()
    {
        var other = new Proposal(_unitB, "theirs");
        await SeedAsync(_creator, [other]);
        var access = new TestRecordAccess().Grant(_reader, SamplePermissions.ProposalsRead, RecordAccess.Within([_unitA], []));
        await using var provider = Services(_reader, access, s => s.AddProposalPolicy());
        await using var scope = provider.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IRecordPolicies>();

        var outside = await policies.FindAsync<Proposal>(other.Id, RecordActions.Read, TestContext.Current.CancellationToken);
        var outsideUpdate = await policies.FindAsync<Proposal>(other.Id, RecordActions.Update, TestContext.Current.CancellationToken);
        var missing = await policies.FindAsync<Proposal>(Ids.New(), RecordActions.Read, TestContext.Current.CancellationToken);

        Assert.Equal(RecordErrors.NotFound, outside.Error.Code);
        Assert.Equal(ErrorKind.NotFound, outside.Error.Kind);
        Assert.Equal(RecordErrors.NotFound, outsideUpdate.Error.Code);
        Assert.Equal(outside.Error.Message, missing.Error.Message);
    }

    [Fact]
    [Proves("ADR-0032")]
    public async Task AVisibleRecordTheReaderMayNotUpdateIsForbidden()
    {
        var draft = new Proposal(_unitA, "draft");
        await SeedAsync(_creator, [draft]);
        var access = new TestRecordAccess().Grant(_reader, SamplePermissions.ProposalsRead, RecordAccess.Within([_unitA], []));
        await using var provider = Services(_reader, access, s => s.AddProposalPolicy());
        await using var scope = provider.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IRecordPolicies>();

        var read = await policies.FindAsync<Proposal>(draft.Id, RecordActions.Read, TestContext.Current.CancellationToken);
        var update = await policies.FindAsync<Proposal>(draft.Id, RecordActions.Update, TestContext.Current.CancellationToken);
        var delete = await policies.FindAsync<Proposal>(draft.Id, RecordActions.Delete, TestContext.Current.CancellationToken);

        Assert.True(read.IsSuccess);
        Assert.Equal(RecordErrors.Forbidden, update.Error.Code);
        Assert.Equal(ErrorKind.Forbidden, update.Error.Kind);
        // No block has a rule for delete: what no rule allows is denied.
        Assert.Equal(RecordErrors.Forbidden, delete.Error.Code);
    }

    [Fact]
    [Proves("ADR-0032")]
    public async Task TheCreatorMayUpdateADraftAndSaveItButNotASubmittedRecord()
    {
        var draft = new Proposal(_unitA, "draft");
        var submitted = new Proposal(_unitA, "submitted");
        submitted.Submit();
        await SeedAsync(_creator, [draft, submitted]);
        var access = new TestRecordAccess().Grant(_creator, SamplePermissions.ProposalsRead, RecordAccess.Within([_unitA], []));
        await using var provider = Services(_creator, access, s => s.AddProposalPolicy());

        await using (var scope = provider.CreateAsyncScope())
        {
            var policies = scope.ServiceProvider.GetRequiredService<IRecordPolicies>();
            var mine = await policies.FindAsync<Proposal>(draft.Id, RecordActions.Update, TestContext.Current.CancellationToken);
            var late = await policies.FindAsync<Proposal>(submitted.Id, RecordActions.Update, TestContext.Current.CancellationToken);

            Assert.True(mine.IsSuccess, mine.IsFailure ? mine.Error.Message : null);
            Assert.Equal(RecordErrors.Forbidden, late.Error.Code);
            mine.Value.Retitle("draft, retitled");
            await scope.ServiceProvider.GetRequiredService<SampleDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Proposals.AsNoTracking().SingleAsync(p => p.Id == draft.Id, TestContext.Current.CancellationToken);
            Assert.Equal("draft, retitled", saved.Title);
        }
    }

    [Fact]
    [Proves("TF-035 AC1", "ADR-0032")]
    public async Task APolicyNarrowsTheScopeRuleAndNeverWidensIt()
    {
        var anvil = new Widget(_unitA, "anvil");
        var bolt = new Widget(_unitA, "bolt");
        var elsewhere = new Widget(_unitB, "elsewhere");
        await SeedAsync(_creator, [anvil, bolt, elsewhere]);
        var access = new TestRecordAccess().Grant(_reader, SamplePermissions.WidgetsRead, RecordAccess.Within([_unitA], []));

        // A policy that would let everyone read and do everything: the scope rule still keeps the other unit out.
        var everything = new FixedPolicy<Widget>(_ => true, PolicyDecision.Allowed);
        var wide = await ReadAsync(access, everything, elsewhere.Id);
        // A policy that reads only names starting with "a": it takes records away inside the scope.
        var narrow = await ReadAsync(access, new FixedPolicy<Widget>(w => EF.Functions.Like(w.Name, "a%"), PolicyDecision.Allowed), elsewhere.Id);

        Assert.Equal(["anvil", "bolt"], wide.Names);
        Assert.Equal(RecordErrors.NotFound, wide.Elsewhere.Error.Code);
        Assert.Equal(["anvil"], narrow.Names);
    }

    [Fact]
    [Proves("TF-035 AC1", "ADR-0032")]
    public async Task ARecordGrantedByItselfIsReadableOutsideTheReadersUnits()
    {
        var granted = new Widget(_unitB, "granted");
        var other = new Widget(_unitB, "other");
        await SeedAsync(_creator, [granted, other]);
        var access = new TestRecordAccess()
            .Grant(_reader, SamplePermissions.WidgetsRead, RecordAccess.Within([_unitA], [granted.Id]))
            .Grant(_reader, SamplePermissions.WidgetsManage, RecordAccess.Within([], [granted.Id]));
        await using var provider = Services(_reader, access, s => s.AddWidgetPolicy());
        await using var scope = provider.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IRecordPolicies>();

        var update = await policies.FindAsync<Widget>(granted.Id, RecordActions.Update, TestContext.Current.CancellationToken);
        var hidden = await policies.FindAsync<Widget>(other.Id, RecordActions.Read, TestContext.Current.CancellationToken);

        Assert.True(update.IsSuccess);
        Assert.Equal(RecordErrors.NotFound, hidden.Error.Code);
    }

    [Fact]
    [Proves("ADR-0032")]
    public async Task ARecordTypeWithoutAPolicyCannotBeListedOrOpened()
    {
        await using var provider = Services(_reader, new TestRecordAccess(), _ => { });
        await using var scope = provider.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IRecordPolicies>();

        var listing = await Assert.ThrowsAsync<InvalidOperationException>(() => policies.ApplyAsync(scope.ServiceProvider.GetRequiredService<SampleDbContext>().Widgets, TestContext.Current.CancellationToken));
        var twice = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWidgetPolicy().AddWidgetPolicy());

        Assert.Contains("No record policy is registered for Widget", listing.Message, StringComparison.Ordinal);
        Assert.Contains("a record type has one policy", twice.Message, StringComparison.Ordinal);
    }

    private async Task<(List<string> Names, Result<Widget> Elsewhere)> ReadAsync(TestRecordAccess access, IRecordPolicy<Widget> policy, Guid elsewhere)
    {
        await using var provider = Services(_reader, access, s => s.AddRecordPolicy(SamplePolicies.WidgetType, "Widgets", SamplePermissions.WidgetsRead, policy));
        await using var scope = provider.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IRecordPolicies>();
        var policed = await policies.ApplyAsync(scope.ServiceProvider.GetRequiredService<SampleDbContext>().Widgets, TestContext.Current.CancellationToken);
        var names = (await policed.Where(w => w.OwningOrganisationId == _unitA || w.OwningOrganisationId == _unitB).ToListAsync(TestContext.Current.CancellationToken)).Select(w => w.Name).Order(StringComparer.Ordinal).ToList();
        return (names, await policies.FindAsync<Widget>(elsewhere, RecordActions.Update, TestContext.Current.CancellationToken));
    }

    private async Task SeedAsync(Guid creator, IReadOnlyList<object> records)
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(creator));
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        context.AddRange(records);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private ServiceProvider Services(Guid principal, TestRecordAccess access, Action<IServiceCollection> policies) =>
        fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(principal), services =>
        {
            services.AddSingleton<IRecordAccess>(access);
            policies(services);
        });
}
