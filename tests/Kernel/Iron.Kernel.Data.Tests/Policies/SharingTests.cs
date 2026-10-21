using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Data.Policies;
using Iron.Kernel.Data.Sharing;
using Iron.Kernel.Data.Tests.Sample;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Data.Tests.Policies;

/// <summary>The units that frame a reader, as Authorization would answer them from the tree.</summary>
public sealed class TestUnitReach : IUnitReach
{
    private readonly Dictionary<Guid, IReadOnlySet<Guid>> _home = [];
    private readonly Dictionary<Guid, IReadOnlySet<Guid>> _working = [];

    public TestUnitReach Home(Guid principal, params Guid[] units)
    {
        _home[principal] = units.ToHashSet();
        return this;
    }

    public TestUnitReach Working(Guid principal, params Guid[] units)
    {
        _working[principal] = units.ToHashSet();
        return this;
    }

    public Task<IReadOnlySet<Guid>> HomeUnitsAsync(Guid principalId, CancellationToken cancellationToken) =>
        Task.FromResult(_home.GetValueOrDefault(principalId) ?? new HashSet<Guid>());

    public Task<IReadOnlySet<Guid>?> WorkingUnitsAsync(Guid principalId, CancellationToken cancellationToken) =>
        Task.FromResult(_working.GetValueOrDefault(principalId));
}

/// <summary>What the Audit module would write, kept in memory.</summary>
public sealed class RecordingRecordAudit : IRecordAudit
{
    public List<RecordReadEvent> Reads { get; } = [];

    public List<RecordRefusedEvent> Refusals { get; } = [];

    public List<RecordsListedEvent> Lists { get; } = [];

    public List<RecordSharingEvent> Sharing { get; } = [];

    public Task ReadAsync(RecordReadEvent read, CancellationToken cancellationToken)
    {
        Reads.Add(read);
        return Task.CompletedTask;
    }

    public Task RefusedAsync(RecordRefusedEvent refused, CancellationToken cancellationToken)
    {
        Refusals.Add(refused);
        return Task.CompletedTask;
    }

    public Task ListedAsync(RecordsListedEvent listed, CancellationToken cancellationToken)
    {
        Lists.Add(listed);
        return Task.CompletedTask;
    }

    public Task SharingChangedAsync(RecordSharingEvent change, CancellationToken cancellationToken)
    {
        Sharing.Add(change);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Sharing modes, the cross-unit read audit and the working unit against PostgreSQL (TF-222, ADR-0038), on the sample
/// module's widgets: each mode in lists and by id with the paging counts, the audit of a read through a scope and
/// through a share, a refused read, one event per list page, and lists that keep to the working unit.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SharingTests(PostgresFixture fixture)
{
    private readonly Guid _unitA = Ids.New();
    private readonly Guid _unitB = Ids.New();
    private readonly Guid _unitC = Ids.New();
    private readonly Guid _reader = Ids.New();
    private readonly Guid _manager = Ids.New();

    [Fact]
    [Proves("TF-035 AC1", "TF-035 AC2", "TF-220 AC1", "TF-220 AC2", "ADR-0038")]
    public async Task EachModeDecidesWhoSeesTheRecordInListsAndByIdAndThePageCountsFollow()
    {
        var mineA = new Widget(_unitA, "a private");
        var privateB = new Widget(_unitB, "b private");
        var sharedWithA = new Widget(_unitB, "b shared with a");
        var sharedWithC = new Widget(_unitB, "b shared with c");
        var tenantB = new Widget(_unitB, "b tenant");
        await SeedAsync(mineA, privateB, sharedWithA, sharedWithC, tenantB);
        await ShareAsync(sharedWithA, SharingMode.Shared, _unitA);
        await ShareAsync(sharedWithC, SharingMode.Shared, _unitC);
        await ShareAsync(tenantB, SharingMode.Tenant);
        var access = new TestRecordAccess().Grant(_reader, SamplePermissions.WidgetsRead, RecordAccess.Within([_unitA], []));

        await using var provider = Services(_reader, access);
        await using var scope = provider.CreateAsyncScope();
        var page = await PageAsync(scope, pageSize: 2);
        var policies = scope.ServiceProvider.GetRequiredService<IRecordPolicies>();

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.Equal(["a private", "b shared with a"], page.Items.Select(w => w.Name));
        Assert.True((await policies.FindAsync<Widget>(sharedWithA.Id, RecordActions.Read, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await policies.FindAsync<Widget>(tenantB.Id, RecordActions.Read, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(RecordErrors.NotFound, (await policies.FindAsync<Widget>(privateB.Id, RecordActions.Read, TestContext.Current.CancellationToken)).Error.Code);
        Assert.Equal(RecordErrors.NotFound, (await policies.FindAsync<Widget>(sharedWithC.Id, RecordActions.Read, TestContext.Current.CancellationToken)).Error.Code);

        // A share lets the unit see the record, not change it: that still needs the permission over the record's unit.
        var manageA = new TestRecordAccess()
            .Grant(_reader, SamplePermissions.WidgetsRead, RecordAccess.Within([_unitA], []))
            .Grant(_reader, SamplePermissions.WidgetsManage, RecordAccess.Within([_unitA], []));
        await using var managing = Services(_reader, manageA);
        await using var managingScope = managing.CreateAsyncScope();
        Assert.Equal(RecordErrors.Forbidden, (await managingScope.ServiceProvider.GetRequiredService<IRecordPolicies>().FindAsync<Widget>(sharedWithA.Id, RecordActions.Update, TestContext.Current.CancellationToken)).Error.Code);

        // The tenant mode is for whoever may read widgets somewhere; nobody else sees anything.
        await using var stranger = Services(Ids.New(), new TestRecordAccess());
        await using var strangerScope = stranger.CreateAsyncScope();
        Assert.Equal(0, (await PageAsync(strangerScope, pageSize: 10)).TotalCount);
    }

    [Fact]
    [Proves("ADR-0038")]
    public async Task AReadByIdFromOutsideTheHomeUnitIsAuditedWithWhatAllowedItAndAReadAtHomeIsNot()
    {
        var atHome = new Widget(_unitA, "home");
        var byScope = new Widget(_unitB, "by scope");
        var byShare = new Widget(_unitC, "by share");
        var tenant = new Widget(_unitC, "tenant");
        await SeedAsync(atHome, byScope, byShare, tenant);
        await ShareAsync(byShare, SharingMode.Shared, _unitA);
        await ShareAsync(tenant, SharingMode.Tenant);
        var access = new TestRecordAccess().Grant(_reader, SamplePermissions.WidgetsRead, RecordAccess.Within([_unitA, _unitB], []));
        var audit = new RecordingRecordAudit();

        await using var provider = Services(_reader, access, new TestUnitReach().Home(_reader, _unitA), audit);
        await using var scope = provider.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IRecordPolicies>();
        foreach (var widget in new[] { atHome, byScope, byShare, tenant })
        {
            Assert.True((await policies.FindAsync<Widget>(widget.Id, RecordActions.Read, TestContext.Current.CancellationToken)).IsSuccess);
        }

        Assert.Equal(
            [(byScope.Id, _unitB, VisibleBy.Scope), (byShare.Id, _unitC, VisibleBy.Share)],
            audit.Reads.Select(r => (r.RecordId, r.OwningUnitId, r.AllowedBy)));
        Assert.All(audit.Reads, r => Assert.Equal((_reader, SamplePolicies.WidgetType, RecordActions.Read), (r.ReaderId, r.RecordType, r.Action)));
    }

    [Fact]
    [Proves("TF-035 AC1", "TF-222 AC2", "ADR-0038")]
    public async Task ARefusedReadIsAuditedAsTheReaderWasAnsweredAndAnIdOfNoRecordIsNot()
    {
        var theirs = new Widget(_unitB, "theirs");
        var mine = new Widget(_unitA, "mine");
        await SeedAsync(theirs, mine);
        var access = new TestRecordAccess().Grant(_reader, SamplePermissions.WidgetsRead, RecordAccess.Within([_unitA], []));
        var audit = new RecordingRecordAudit();

        await using var provider = Services(_reader, access, new TestUnitReach().Home(_reader, _unitA), audit);
        await using var scope = provider.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IRecordPolicies>();
        var hidden = await policies.FindAsync<Widget>(theirs.Id, RecordActions.Read, TestContext.Current.CancellationToken);
        var forbidden = await policies.FindAsync<Widget>(mine.Id, RecordActions.Update, TestContext.Current.CancellationToken);
        var missing = await policies.FindAsync<Widget>(Ids.New(), RecordActions.Read, TestContext.Current.CancellationToken);

        Assert.Equal(RecordErrors.NotFound, hidden.Error.Code);
        Assert.Equal(RecordErrors.Forbidden, forbidden.Error.Code);
        Assert.Equal(RecordErrors.NotFound, missing.Error.Code);
        Assert.Equal(
            [(theirs.Id, _unitB, RecordActions.Read, RecordAnswers.NotFound), (mine.Id, _unitA, RecordActions.Update, RecordAnswers.Forbidden)],
            audit.Refusals.Select(r => (r.RecordId, r.OwningUnitId, r.Action, r.Answer)));
        Assert.Empty(audit.Reads);
    }

    [Fact]
    [Proves("ADR-0038")]
    public async Task AListPageWithRecordsOfOtherUnitsWritesOneEventWithTheirIdsAndCount()
    {
        var atHome = new Widget(_unitA, "1 home");
        var elsewhere = new Widget(_unitB, "2 elsewhere");
        var shared = new Widget(_unitC, "3 shared");
        var tenant = new Widget(_unitC, "4 tenant");
        await SeedAsync(atHome, elsewhere, shared, tenant);
        await ShareAsync(shared, SharingMode.Shared, _unitA);
        await ShareAsync(tenant, SharingMode.Tenant);
        var access = new TestRecordAccess().Grant(_reader, SamplePermissions.WidgetsRead, RecordAccess.Within([_unitA, _unitB], []));
        var audit = new RecordingRecordAudit();

        await using var provider = Services(_reader, access, new TestUnitReach().Home(_reader, _unitA), audit);
        await using var scope = provider.CreateAsyncScope();
        var page = await PageAsync(scope, pageSize: 10);

        Assert.Equal(4, page.TotalCount);
        var listed = Assert.Single(audit.Lists);
        Assert.Equal((_reader, SamplePolicies.WidgetType), (listed.ReaderId, listed.RecordType));
        Assert.Equal(new[] { elsewhere.Id, shared.Id }.Order(), listed.RecordIds.Order());
        Assert.Empty(audit.Reads);
    }

    [Fact]
    [Proves("ADR-0038")]
    public async Task ListsKeepToTheWorkingUnitAndBelowWhileAReadByIdDoesNot()
    {
        var inA = new Widget(_unitA, "in a");
        var inB = new Widget(_unitB, "in b");
        var sharedWithA = new Widget(_unitC, "c shared with a");
        var tenant = new Widget(_unitC, "c tenant");
        await SeedAsync(inA, inB, sharedWithA, tenant);
        await ShareAsync(sharedWithA, SharingMode.Shared, _unitA);
        await ShareAsync(tenant, SharingMode.Tenant);
        var access = new TestRecordAccess().Grant(_reader, SamplePermissions.WidgetsRead, RecordAccess.Within([_unitA, _unitB, _unitC], []));

        await using var working = Services(_reader, access, new TestUnitReach().Working(_reader, _unitA));
        await using var workingScope = working.CreateAsyncScope();
        var inWorkingUnit = await PageAsync(workingScope, pageSize: 10);
        var other = await workingScope.ServiceProvider.GetRequiredService<IRecordPolicies>().FindAsync<Widget>(inB.Id, RecordActions.Read, TestContext.Current.CancellationToken);

        await using var consolidated = Services(_reader, access, new TestUnitReach());
        await using var consolidatedScope = consolidated.CreateAsyncScope();
        var everything = await PageAsync(consolidatedScope, pageSize: 10);

        Assert.Equal(["c shared with a", "c tenant", "in a"], inWorkingUnit.Items.Select(w => w.Name));
        Assert.True(other.IsSuccess);
        Assert.Equal(4, everything.TotalCount);
    }

    [Fact]
    [Proves("ADR-0038")]
    public async Task ChangingTheSharingIsAPolicyActionAuditedWithTheUnitsBeforeAndAfter()
    {
        var widget = new Widget(_unitB, "to share");
        await SeedAsync(widget);
        var audit = new RecordingRecordAudit();
        var manager = new TestRecordAccess()
            .Grant(_manager, SamplePermissions.WidgetsRead, RecordAccess.Within([_unitB], []))
            .Grant(_manager, SamplePermissions.WidgetsManage, RecordAccess.Within([_unitB], []));
        var readerOnly = new TestRecordAccess().Grant(_reader, SamplePermissions.WidgetsRead, RecordAccess.Within([_unitB], []));

        await using var asReader = Services(_reader, readerOnly);
        await using var readerScope = asReader.CreateAsyncScope();
        var refused = await readerScope.ServiceProvider.GetRequiredService<IRecordSharing>().ChangeAsync(SamplePolicies.WidgetType, widget.Id, SharingMode.Tenant, [], TestContext.Current.CancellationToken);

        await using var asManager = Services(_manager, manager, audit: audit);
        await using var managerScope = asManager.CreateAsyncScope();
        var sharing = managerScope.ServiceProvider.GetRequiredService<IRecordSharing>();
        var empty = await sharing.ChangeAsync(SamplePolicies.WidgetType, widget.Id, SharingMode.Shared, [_unitB], TestContext.Current.CancellationToken);
        var shared = await sharing.ChangeAsync(SamplePolicies.WidgetType, widget.Id, SharingMode.Shared, [_unitA, _unitC, _unitA], TestContext.Current.CancellationToken);
        var narrowed = await sharing.ChangeAsync(SamplePolicies.WidgetType, widget.Id, SharingMode.Shared, [_unitC], TestContext.Current.CancellationToken);
        var unknown = await sharing.ChangeAsync("sample_nothing", widget.Id, SharingMode.Tenant, [], TestContext.Current.CancellationToken);

        Assert.Equal(RecordErrors.Forbidden, refused.Error.Code);
        Assert.Equal(SharingErrors.UnitsRequired, empty.Error.Code);
        Assert.Equal(new[] { _unitA, _unitC }.Order(), shared.Value.Units);
        Assert.Equal([_unitC], narrowed.Value.Units);
        Assert.Equal(SharingErrors.RecordTypeUnknown, unknown.Error.Code);
        Assert.Equal(SharingMode.Shared, (await sharing.GetAsync(SamplePolicies.WidgetType, widget.Id, TestContext.Current.CancellationToken)).Value.Mode);
        Assert.Equal(
            [(SharingMode.Private, 0, SharingMode.Shared, 2), (SharingMode.Shared, 2, SharingMode.Shared, 1)],
            audit.Sharing.Select(s => (s.ModeBefore, s.UnitsBefore.Count, s.ModeAfter, s.UnitsAfter.Count)));
        Assert.All(audit.Sharing, s => Assert.Equal((_manager, widget.Id, _unitB), (s.ActorId, s.RecordId, s.OwningUnitId)));
    }

    [Fact]
    [Proves("ADR-0038")]
    public async Task ANewRecordTakesTheWorkingUnitUnlessTheCreatorChoseAnother()
    {
        var working = new FixedPrincipal(_reader, _unitA);

        Assert.Equal(_unitA, NewRecords.OwningUnit(working));
        Assert.Equal(_unitB, NewRecords.OwningUnit(working, _unitB));
        Assert.Null(NewRecords.OwningUnit(new FixedPrincipal(_reader)));
        await SeedAsync(new Widget(NewRecords.OwningUnit(working)!.Value, "made in a"));
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), working);
        await using var scope = provider.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Widgets.AnyAsync(w => w.Name == "made in a" && w.OwningOrganisationId == _unitA, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0038")]
    public async Task AUnitsRecordsAndSharesMoveToAnotherUnitForAMerge()
    {
        var widget = new Widget(_unitB, "moves");
        var proposal = new Proposal(_unitB, "moves too");
        var stays = new Widget(_unitC, "stays");
        await SeedAsync(widget, proposal, stays);
        await ShareAsync(stays, SharingMode.Shared, _unitB);

        await using var provider = Services(_manager, new TestRecordAccess(), configure: s => s.AddUnitRecordOwners<SampleDbContext>("sample"));
        await using var scope = provider.CreateAsyncScope();
        var owners = Assert.Single(scope.ServiceProvider.GetServices<IUnitRecordOwners>());
        var shares = scope.ServiceProvider.GetRequiredService<IRecordShares>();
        var preview = await owners.OwnedByAsync(_unitB, TestContext.Current.CancellationToken);
        var moved = await owners.MoveAsync(_unitB, _unitA, TestContext.Current.CancellationToken);
        var sharesMoved = await shares.MoveAsync(_unitB, _unitA, TestContext.Current.CancellationToken);

        Assert.Equal("sample", owners.Module);
        Assert.Equal([(SamplePolicies.ProposalType, "Proposals", 1), (SamplePolicies.WidgetType, "Widgets", 1)], preview.Select(r => (r.RecordType, r.DisplayName, r.RecordIds.Count)).Order());
        Assert.Equal(preview.SelectMany(r => r.RecordIds).Order(), moved.SelectMany(r => r.RecordIds).Order());
        Assert.Equal([stays.Id], sharesMoved.Select(s => s.RecordId));
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        Assert.Equal(_unitA, (await context.Widgets.AsNoTracking().SingleAsync(w => w.Id == widget.Id, TestContext.Current.CancellationToken)).OwningOrganisationId);
        Assert.Equal(_unitA, (await context.Proposals.AsNoTracking().SingleAsync(p => p.Id == proposal.Id, TestContext.Current.CancellationToken)).OwningOrganisationId);
        Assert.Contains(await shares.OfUnitAsync(_unitA, TestContext.Current.CancellationToken), s => s.RecordId == stays.Id);
        Assert.DoesNotContain(await shares.OfUnitAsync(_unitB, TestContext.Current.CancellationToken), s => s.RecordId == stays.Id);
    }

    private async Task<Page<Widget>> PageAsync(AsyncServiceScope scope, int pageSize)
    {
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var fields = new ListFields<Widget>().Add("name", w => w.Name).SortByDefault("name");
        var query = new ListQuery(new PageRequest(1, pageSize), [], [], []);
        var units = new[] { _unitA, _unitB, _unitC };
        var policed = await scope.ServiceProvider.GetRequiredService<IRecordPolicies>().ApplyAsync(context.Widgets.AsNoTracking(), TestContext.Current.CancellationToken);
        return await policed.Where(w => units.Contains(w.OwningOrganisationId)).ApplyListQuery(query, fields).ToPageAsync(query.Page, TestContext.Current.CancellationToken);
    }

    private async Task SeedAsync(params Widget[] widgets) => await SeedAsync(widgets.Cast<object>().ToArray());

    private async Task SeedAsync(params object[] records)
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(_manager));
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        context.AddRange(records);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Shares through the kernel's sharing service, as a manager of the record's unit would.</summary>
    private async Task ShareAsync(Widget widget, SharingMode mode, params Guid[] units)
    {
        var manager = new TestRecordAccess()
            .Grant(_manager, SamplePermissions.WidgetsRead, RecordAccess.Within([widget.OwningOrganisationId], []))
            .Grant(_manager, SamplePermissions.WidgetsManage, RecordAccess.Within([widget.OwningOrganisationId], []));
        await using var provider = Services(_manager, manager);
        await using var scope = provider.CreateAsyncScope();
        var changed = await scope.ServiceProvider.GetRequiredService<IRecordSharing>().ChangeAsync(SamplePolicies.WidgetType, widget.Id, mode, units, TestContext.Current.CancellationToken);
        Assert.True(changed.IsSuccess, changed.IsFailure ? changed.Error.Message : null);
    }

    private ServiceProvider Services(Guid principal, TestRecordAccess access, IUnitReach? units = null, RecordingRecordAudit? audit = null, Action<IServiceCollection>? configure = null) =>
        fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(principal), services =>
        {
            services.AddSingleton<IRecordAccess>(access);
            services.AddWidgetPolicy();
            services.AddProposalPolicy();
            if (units is not null)
            {
                services.AddSingleton(units);
            }

            if (audit is not null)
            {
                services.AddSingleton<IRecordAudit>(audit);
            }

            configure?.Invoke(services);
        });
}
