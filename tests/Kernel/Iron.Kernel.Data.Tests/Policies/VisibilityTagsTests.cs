using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.History;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Data.Policies;
using Iron.Kernel.Data.Sharing;
using Iron.Kernel.Data.Tests.Sample;
using Iron.Kernel.Data.Visibility;
using Iron.Kernel.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Data.Tests.Policies;

/// <summary>The tags the Visibility module would answer, kept in memory.</summary>
public sealed class TestVisibilityTags : IVisibilityTags
{
    private readonly Dictionary<string, VisibilityTagInfo> _tags = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, IReadOnlyList<string>> _held = [];

    public TestVisibilityTags Define(string code, bool active = true)
    {
        _tags[code] = new VisibilityTagInfo(code, $"The {code} tag", active);
        return this;
    }

    public TestVisibilityTags Hold(Guid principal, params string[] codes)
    {
        _held[principal] = codes;
        return this;
    }

    public Task<IReadOnlyList<string>> HeldAsync(Guid principalId, CancellationToken cancellationToken) =>
        Task.FromResult(_held.GetValueOrDefault(principalId) ?? []);

    public Task<IReadOnlyDictionary<string, VisibilityTagInfo>> FindAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, VisibilityTagInfo>>(codes.Where(_tags.ContainsKey).Distinct().ToDictionary(c => c, c => _tags[c], StringComparer.Ordinal));

    public Task<IReadOnlyList<VisibilityTagInfo>> ActiveAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VisibilityTagInfo>>([.. _tags.Values.Where(t => t.Active).OrderBy(t => t.Code, StringComparer.Ordinal)]);
}

/// <summary>
/// Visibility tags in the record policy's query against PostgreSQL (TF-241, ADR-0057), on the sample module: the filter
/// after the scope rule and the policy, in lists and by id, with the paging totals; the explanation's tag check; rule 5
/// when tags are put on a record or taken off; a retired tag; the filter beside a share and the working unit; the field
/// history of a change; and an order that takes its supplier's tags (rule 6).
/// </summary>
[Collection(PostgresCollection.Name)]
public class VisibilityTagsTests(PostgresFixture fixture)
{
    private readonly Guid _unitA = Ids.New();
    private readonly Guid _unitB = Ids.New();
    private readonly Guid _reader = Ids.New();
    private readonly Guid _manager = Ids.New();

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-241 AC1", "TF-241 AC3")]
    public async Task AReaderHoldingOneOfARecordsTagsSeesItAndEveryoneWhoPassesRolesAndUnitsSeesAnUntaggedOne()
    {
        var both = new Widget(_unitA, "apac and latam");
        var untagged = new Widget(_unitA, "untagged");
        var latam = new Widget(_unitA, "latam only");
        var elsewhere = new Widget(_unitB, "apac, another unit");
        await SeedAsync(both, untagged, latam, elsewhere);
        await TagAsync(both, "APAC", "LATAM");
        await TagAsync(latam, "LATAM");
        await TagAsync(elsewhere, "APAC");

        await using var provider = Reader(holds: ["APAC"]);
        var page = await PageAsync(provider, pageSize: 10);

        // The record of another unit stays out: the tags narrow what roles and units show, never more.
        Assert.Equal(["apac and latam", "untagged"], page.Items.Select(w => w.Name));
    }

    [Fact]
    [Proves("TF-241 AC2", "ADR-0039")]
    public async Task ARecordOfAnotherTagAnswersNotFoundByIdAndTheExplanationComparesTheTags()
    {
        var latam = new Widget(_unitA, "latam");
        var mixed = new Widget(_unitA, "latam and emea");
        await SeedAsync(latam, mixed);
        await TagAsync(latam, "LATAM");
        await TagAsync(mixed, "LATAM", "EMEA");

        await using var provider = Reader(holds: ["APAC", "EMEA"]);
        await using var scope = provider.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IRecordPolicies>();
        var found = await policies.FindAsync<Widget>(latam.Id, RecordActions.Read, Cancel);
        var explained = await policies.For<Widget>(_reader).ExplainAsync(latam.Id, RecordActions.Read, Cancel);
        var shared = await policies.For<Widget>(_reader).ExplainAsync(mixed.Id, RecordActions.Read, Cancel);

        Assert.Equal(RecordErrors.NotFound, found.Error.Code);
        Assert.Equal((RecordPolicyAnswers.NotFound, true), (explained.Answer, explained.Exists));
        Assert.False(explained.Tags!.Passed);
        Assert.Equal(["LATAM"], explained.Tags.RecordTags);
        Assert.Equal(["APAC", "EMEA"], explained.Tags.HeldTags);
        Assert.Empty(explained.Tags.SharedTags);
        Assert.Equal(RecordPolicyAnswers.Allowed, shared.Answer);
        Assert.Equal(["EMEA"], shared.Tags!.SharedTags);
        Assert.True(shared.Tags.Passed);
    }

    [Fact]
    [Proves("TF-241 AC4")]
    public async Task AReaderWhoHoldsNoTagsSeesTaggedAndUntaggedRecords()
    {
        var tagged = new Widget(_unitA, "tagged");
        var untagged = new Widget(_unitA, "untagged");
        await SeedAsync(tagged, untagged);
        await TagAsync(tagged, "LATAM");

        await using var provider = Reader(holds: []);
        var page = await PageAsync(provider, pageSize: 10);
        await using var scope = provider.CreateAsyncScope();

        Assert.Equal(["tagged", "untagged"], page.Items.Select(w => w.Name));
        Assert.True((await scope.ServiceProvider.GetRequiredService<IRecordPolicies>().FindAsync<Widget>(tagged.Id, RecordActions.Read, Cancel)).IsSuccess);
    }

    [Fact]
    [Proves("TF-241 AC5", "ADR-0057")]
    public async Task APersonPutsOnARecordOnlyTheTagsTheyHoldAndTheRefusalNamesTheTag()
    {
        var widget = new Widget(_unitA, "to tag");
        await SeedAsync(widget);
        await TagAsync(widget, "APAC", "EMEA");

        await using var holder = Manager(holds: ["APAC"]);
        await using var scope = holder.CreateAsyncScope();
        var tags = scope.ServiceProvider.GetRequiredService<IRecordTags>();
        var put = await tags.ChangeAsync(SamplePolicies.WidgetType, widget.Id, ["APAC", "EMEA", "LATAM"], Cancel);
        var takenOff = await tags.ChangeAsync(SamplePolicies.WidgetType, widget.Id, ["APAC"], Cancel);
        var view = await tags.GetAsync(SamplePolicies.WidgetType, widget.Id, Cancel);

        Assert.Equal(VisibilityErrors.TagNotHeld, put.Error.Code);
        Assert.Contains("LATAM", put.Error.Message, StringComparison.Ordinal);
        Assert.Equal(VisibilityErrors.TagNotHeld, takenOff.Error.Code);
        Assert.Contains("EMEA", takenOff.Error.Message, StringComparison.Ordinal);
        Assert.Equal(["APAC", "EMEA"], view.Value.Tags.Select(t => t.Code));
        Assert.Equal(["APAC"], view.Value.Choices.Select(t => t.Code));
        Assert.True(view.Value.MayChange);

        // Whoever holds no tags puts any active tag, and a code is kept in capitals.
        await using var anyone = Manager(holds: []);
        await using var anyoneScope = anyone.CreateAsyncScope();
        var changed = await anyoneScope.ServiceProvider.GetRequiredService<IRecordTags>().ChangeAsync(SamplePolicies.WidgetType, widget.Id, ["apac", " latam "], Cancel);
        Assert.Equal(["APAC", "LATAM"], changed.Value.Tags.Select(t => t.Code));
        Assert.Equal(["APAC", "EMEA", "LATAM", "NA"], changed.Value.Choices.Select(t => t.Code));
    }

    [Fact]
    [Proves("ADR-0057")]
    public async Task ChangingARecordsTagsIsAPolicyActionAndAnUnknownTagIsRefusedByName()
    {
        var widget = new Widget(_unitA, "guarded");
        await SeedAsync(widget);

        await using var reader = Reader(holds: []);
        await using var readerScope = reader.CreateAsyncScope();
        var tags = readerScope.ServiceProvider.GetRequiredService<IRecordTags>();
        var refused = await tags.ChangeAsync(SamplePolicies.WidgetType, widget.Id, ["APAC"], Cancel);
        var view = await tags.GetAsync(SamplePolicies.WidgetType, widget.Id, Cancel);

        await using var manager = Manager(holds: []);
        await using var managerScope = manager.CreateAsyncScope();
        var unknown = await managerScope.ServiceProvider.GetRequiredService<IRecordTags>().ChangeAsync(SamplePolicies.WidgetType, widget.Id, ["NOWHERE"], Cancel);
        var noType = await managerScope.ServiceProvider.GetRequiredService<IRecordTags>().ChangeAsync("sample_nothing", widget.Id, ["APAC"], Cancel);

        Assert.Equal(RecordErrors.Forbidden, refused.Error.Code);
        Assert.False(view.Value.MayChange);
        Assert.Empty(view.Value.Choices);
        Assert.Equal(VisibilityErrors.TagUnknown, unknown.Error.Code);
        Assert.Contains("NOWHERE", unknown.Error.Message, StringComparison.Ordinal);
        Assert.Equal(VisibilityErrors.RecordTypeUnknown, noType.Error.Code);
    }

    [Fact]
    [Proves("TF-241 AC6", "TF-035 AC2")]
    public async Task OfAHundredRecordsTheTotalAndThePagesCountTheFortyTheReaderSeesByTheirTags()
    {
        var widgets = Enumerable.Range(1, 100).Select(n => new Widget(_unitA, $"widget {n:000}")).ToList();
        await SeedAsync([.. widgets]);

        // 20 untagged and 20 carrying APAC are seen; 60 carrying LATAM are not.
        await TagAsync(widgets.Skip(20).Take(20).ToArray(), "APAC");
        await TagAsync(widgets.Skip(40).ToArray(), "LATAM");

        await using var provider = Reader(holds: ["APAC"]);
        var first = await PageAsync(provider, pageSize: 15);
        var last = await PageAsync(provider, pageSize: 15, number: 3);
        await using var scope = provider.CreateAsyncScope();
        var counted = await (await scope.ServiceProvider.GetRequiredService<IRecordPolicies>().ApplyAsync(scope.ServiceProvider.GetRequiredService<SampleDbContext>().Widgets, Cancel))
            .Where(w => w.OwningOrganisationId == _unitA).CountAsync(Cancel);

        Assert.Equal((40, 3), (first.TotalCount, first.TotalPages));
        Assert.Equal(15, first.Items.Count);
        Assert.Equal(10, last.Items.Count);
        Assert.Equal(40, counted);
        Assert.All(last.Items, w => Assert.DoesNotContain("LATAM", w.VisibilityTags));
    }

    [Fact]
    [Proves("TF-241 AC7", "ADR-0051")]
    public async Task ATagChangeOnARecordIsInItsFieldHistoryWithBeforeAndAfter()
    {
        var widget = new Widget(_unitA, "with history");
        await SeedAsync(widget);
        var recorder = new KeptChanges();

        await using var provider = Manager(holds: [], recorder: recorder);
        await using var scope = provider.CreateAsyncScope();
        var tags = scope.ServiceProvider.GetRequiredService<IRecordTags>();
        Assert.True((await tags.ChangeAsync(SamplePolicies.WidgetType, widget.Id, ["LATAM", "APAC"], Cancel)).IsSuccess);
        Assert.True((await tags.ChangeAsync(SamplePolicies.WidgetType, widget.Id, ["APAC"], Cancel)).IsSuccess);

        var changes = recorder.Changes.Where(c => c.RecordId == widget.Id).ToList();
        Assert.Equal([RecordChangeKinds.Changed, RecordChangeKinds.Changed], changes.Select(c => c.Kind));
        Assert.Equal(
            [("visibility_tags", "[]", "[\"APAC\",\"LATAM\"]"), ("visibility_tags", "[\"APAC\",\"LATAM\"]", "[\"APAC\"]")],
            changes.Select(c => Assert.Single(c.Fields)).Select(f => (f.Field, f.Old, f.New)));
    }

    [Fact]
    [Proves("TF-241 AC8", "ADR-0057")]
    public async Task AnOrderTakesItsSuppliersTagsWhenItIsCreatedAndKeepsThemAfterward()
    {
        var supplier = new SampleSupplier(_unitA, "Tagged supplier");
        var plain = new SampleSupplier(_unitA, "Untagged supplier");
        await SeedAsync(supplier, plain);
        await using (var setup = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(_manager)))
        {
            await using var setupScope = setup.CreateAsyncScope();
            await setupScope.ServiceProvider.GetRequiredService<SampleDbContext>().Suppliers.Where(s => s.Id == supplier.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.VisibilityTags, new[] { "APAC", "LATAM" }), Cancel);
        }

        var recorder = new KeptChanges();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(_manager), services =>
        {
            services.AddSingleton<IRecordAccess>(new TestRecordAccess());
            services.AddSupplierAndOrderPolicies();
            services.AddScoped<IChangeRecorder>(_ => recorder);
        });
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var fromTagged = new SampleOrder(_unitA, supplier.Id, "PO-TAGGED", DateTimeOffset.UtcNow);
        var fromPlain = new SampleOrder(_unitA, plain.Id, "PO-PLAIN", DateTimeOffset.UtcNow);
        context.Orders.AddRange(fromTagged, fromPlain);
        await context.SaveChangesAsync(Cancel);
        await context.Suppliers.Where(s => s.Id == supplier.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.VisibilityTags, new[] { "EMEA" }), Cancel);

        var stored = await context.Orders.AsNoTracking().Where(o => o.Id == fromTagged.Id || o.Id == fromPlain.Id).ToDictionaryAsync(o => o.Reference, o => o.VisibilityTags, Cancel);
        Assert.Equal(["APAC", "LATAM"], stored["PO-TAGGED"]);
        Assert.Empty(stored["PO-PLAIN"]);
        var created = Assert.Single(recorder.Changes, c => c.RecordId == fromTagged.Id);
        Assert.Equal((RecordChangeKinds.Created, "[\"APAC\",\"LATAM\"]"), (created.Kind, created.Fields.Single(f => f.Field == "visibility_tags").New));
    }

    [Fact]
    [Proves("ADR-0057")]
    public async Task ARetiredTagStillHidesItsRecordsAndCanBeTakenOffButNotPutOn()
    {
        var retired = new Widget(_unitA, "retired tag");
        var other = new Widget(_unitA, "other");
        await SeedAsync(retired, other);
        await TagAsync(retired, "OLD");

        await using (var apac = Reader(holds: ["APAC"]))
        {
            Assert.DoesNotContain((await PageAsync(apac, pageSize: 10)).Items, w => w.Id == retired.Id);
        }

        await using (var old = Reader(holds: ["OLD"]))
        {
            Assert.Contains((await PageAsync(old, pageSize: 10)).Items, w => w.Id == retired.Id);
        }

        await using var manager = Manager(holds: []);
        await using var scope = manager.CreateAsyncScope();
        var tags = scope.ServiceProvider.GetRequiredService<IRecordTags>();
        var put = await tags.ChangeAsync(SamplePolicies.WidgetType, other.Id, ["OLD"], Cancel);
        var takenOff = await tags.ChangeAsync(SamplePolicies.WidgetType, retired.Id, [], Cancel);

        Assert.Equal(VisibilityErrors.TagRetired, put.Error.Code);
        Assert.Contains("OLD", put.Error.Message, StringComparison.Ordinal);
        Assert.True(takenOff.IsSuccess);
        Assert.Empty(takenOff.Value.Tags);
    }

    [Fact]
    [Proves("ADR-0038", "ADR-0057")]
    public async Task TheTagsNarrowWhatAShareAndTheWorkingUnitShowAndNeverWidenEither()
    {
        var sharedLatam = new Widget(_unitB, "b shared, latam");
        var sharedApac = new Widget(_unitB, "b shared, apac");
        var inA = new Widget(_unitA, "a, apac");
        var notShared = new Widget(_unitB, "b, apac, not shared");
        await SeedAsync(sharedLatam, sharedApac, inA, notShared);
        await ShareWithAAsync(sharedLatam, sharedApac);
        await TagAsync([sharedApac, inA, notShared], "APAC");
        await TagAsync(sharedLatam, "LATAM");

        // Reads in unit A, works in unit A: a share shows B's records, the tags narrow them, and B's own stays out.
        var reach = new TestRecordAccess().Grant(_reader, SamplePermissions.WidgetsRead, RecordAccess.Within([_unitA], []));
        await using var provider = Services(_reader, reach, new TestVisibilityTags().Define("APAC").Define("LATAM").Hold(_reader, "APAC"), new TestUnitReach().Working(_reader, _unitA));
        var page = await PageAsync(provider, pageSize: 10);
        await using var scope = provider.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredService<IRecordPolicies>();

        Assert.Equal(["a, apac", "b shared, apac"], page.Items.Select(w => w.Name));
        Assert.Equal(RecordErrors.NotFound, (await policies.FindAsync<Widget>(sharedLatam.Id, RecordActions.Read, Cancel)).Error.Code);
        Assert.Equal(RecordErrors.NotFound, (await policies.FindAsync<Widget>(notShared.Id, RecordActions.Read, Cancel)).Error.Code);
    }

    private ServiceProvider Reader(IReadOnlyList<string> holds) =>
        Services(_reader, new TestRecordAccess().Grant(_reader, SamplePermissions.WidgetsRead, RecordAccess.Within([_unitA], [])), Tags().Hold(_reader, [.. holds]));

    private ServiceProvider Manager(IReadOnlyList<string> holds, KeptChanges? recorder = null) =>
        Services(
            _manager,
            new TestRecordAccess()
                .Grant(_manager, SamplePermissions.WidgetsRead, RecordAccess.Within([_unitA, _unitB], []))
                .Grant(_manager, SamplePermissions.WidgetsManage, RecordAccess.Within([_unitA, _unitB], [])),
            Tags().Hold(_manager, [.. holds]),
            recorder: recorder);

    private static TestVisibilityTags Tags() => new TestVisibilityTags().Define("APAC").Define("EMEA").Define("LATAM").Define("NA").Define("OLD", active: false);

    private ServiceProvider Services(Guid principal, TestRecordAccess access, TestVisibilityTags tags, IUnitReach? units = null, KeptChanges? recorder = null) =>
        fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(principal), services =>
        {
            services.AddSingleton<IRecordAccess>(access);
            services.AddSingleton<IVisibilityTags>(tags);
            services.AddWidgetPolicy();
            if (units is not null)
            {
                services.AddSingleton(units);
            }

            if (recorder is not null)
            {
                services.AddScoped<IChangeRecorder>(_ => recorder);
            }
        });

    private async Task<Page<Widget>> PageAsync(ServiceProvider provider, int pageSize, int number = 1)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var fields = new ListFields<Widget>().Add("name", w => w.Name).SortByDefault("name");
        var query = new ListQuery(new PageRequest(number, pageSize), [], [], []);
        var units = new[] { _unitA, _unitB };
        var policed = await scope.ServiceProvider.GetRequiredService<IRecordPolicies>().ApplyAsync(context.Widgets.AsNoTracking(), Cancel);
        return await policed.Where(w => units.Contains(w.OwningOrganisationId)).ApplyListQuery(query, fields).ToPageAsync(query.Page, Cancel);
    }

    private async Task SeedAsync(params object[] records)
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(_manager));
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        context.AddRange(records);
        await context.SaveChangesAsync(Cancel);
    }

    /// <summary>Tags records directly, for the tests that are not about putting them on.</summary>
    private Task TagAsync(Widget widget, params string[] codes) => TagAsync([widget], codes);

    private async Task TagAsync(Widget[] widgets, params string[] codes)
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(_manager));
        await using var scope = provider.CreateAsyncScope();
        var ids = widgets.Select(w => w.Id).ToArray();
        await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Widgets.Where(w => ids.Contains(w.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.VisibilityTags, codes), Cancel);
    }

    private async Task ShareWithAAsync(params Widget[] widgets)
    {
        await using var provider = Manager(holds: []);
        await using var scope = provider.CreateAsyncScope();
        foreach (var widget in widgets)
        {
            var shared = await scope.ServiceProvider.GetRequiredService<IRecordSharing>().ChangeAsync(SamplePolicies.WidgetType, widget.Id, SharingMode.Shared, [_unitA], Cancel);
            Assert.True(shared.IsSuccess, shared.IsFailure ? shared.Error.Message : null);
        }
    }

    /// <summary>The Audit module's part, as the test sees it: every change of every save.</summary>
    private sealed class KeptChanges : IChangeRecorder
    {
        public List<RecordChange> Changes { get; } = [];

        public Task RecordAsync(IReadOnlyList<RecordChange> changes, CancellationToken cancellationToken)
        {
            Changes.AddRange(changes);
            return Task.CompletedTask;
        }
    }
}
