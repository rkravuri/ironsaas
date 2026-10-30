using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Fleet.Data;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Tenancy.Tests;

/// <summary>
/// A tenant's suites in the control plane (TF-244, ADR-0059): created with a tenant or added to an active one with the
/// suite's plan, each change an update job with the operator's reason; removed, its entitlements go and nothing else; the
/// store's document is the union of the Foundation plan and the suites' plans, each entry with its own plan's grace days.
/// The control plane's catalogue is this build's, which carries the eProcurement Suite and its Essentials plan.
/// </summary>
[Collection(TenancyCollection.Name)]
public class SuiteTests(TenancyFixture fixture)
{
    private const string Procurement = "procurement";

    private const string Essentials = "procurement-essentials";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-244 AC3", "TF-244 AC6", "ADR-0059")]
    public async Task AddingASuiteAppliesItsPlanToItsOwnGroupAndRequestsAnAuditedUpdateJob()
    {
        var operatorId = Ids.New();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(operatorId));
        var suffix = $"{Ids.New():N}"[..8];
        var foundationPlan = $"roles-{suffix}";
        var slug = await ActiveTenantAsync(provider, $"add-{suffix}", foundationPlan, ["authorization.role_transfer"]);

        await using (var scope = provider.CreateAsyncScope())
        {
            var suites = scope.ServiceProvider.GetRequiredService<SuiteService>();
            var noReason = await suites.AddAsync(slug, new SuiteAddition(Procurement, null, null, null, " "), Cancel);
            Assert.Equal(TenancyErrors.SuiteReasonRequired, noReason.Error.Code);
            var unknown = await suites.AddAsync(slug, new SuiteAddition("school", null, null, null, "Bought it"), Cancel);
            Assert.Equal(TenancyErrors.SuiteUnknown, unknown.Error.Code);
            var foundation = await suites.AddAsync(slug, new SuiteAddition(Procurement, foundationPlan, null, null, "Bought it"), Cancel);
            Assert.Equal(TenancyErrors.PlanOtherGroup, foundation.Error.Code);

            var added = await suites.AddAsync(slug, new SuiteAddition(Procurement, Essentials, null, null, "Bought the eProcurement Suite"), Cancel);

            Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
            Assert.Equal((Procurement, string.Empty, Essentials), (added.Value.Entitlements.Suite, added.Value.Entitlements.PlanBefore, added.Value.Entitlements.PlanAfter));
            Assert.Equal(["purchasing"], added.Value.Entitlements.Added.Select(a => a.Code));
            Assert.Equal((JobKind.Update, ProvisioningStatus.Requested), (added.Value.Job.Kind, added.Value.Job.Status));
            Assert.Equal((Procurement, "Bought the eProcurement Suite"), (added.Value.Job.Request?.Subject, added.Value.Job.Request?.Reason));
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
            var record = await registry.FindAsync(slug, Cancel);
            Assert.Equal([Procurement], record!.InstalledSuites);
            Assert.Equal(foundationPlan, record.Plan);

            var audit = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.EventType == TenancyAuditEvents.SuiteAdded && e.Target == slug).ToListAsync(Cancel);
            var added = Assert.Single(audit);
            Assert.Equal((operatorId, "Bought the eProcurement Suite"), (added.ActorId, added.Reason));
            Assert.Contains(Essentials, added.Details, StringComparison.Ordinal);

            // TF-244 AC6: the union of the two plans, each entry with its group and its own plan's grace days.
            var document = await scope.ServiceProvider.GetRequiredService<EntitlementService>().DocumentAsync(slug, Cancel);
            Assert.NotNull(document);
            var purchasing = Assert.Single(document.Modules, m => m.Code == "purchasing");
            Assert.Equal((Procurement, 30), (purchasing.Suite, purchasing.GraceDays));
            var roleTransfer = Assert.Single(document.Features, f => f.Code == "authorization.role_transfer");
            Assert.Equal((SuiteName.Foundation, 7), (roleTransfer.Suite, roleTransfer.GraceDays));
            Assert.Equal([new SuitePlan(Procurement, Essentials, 30)], document.Suites);
            Assert.Equal(foundationPlan, document.Plan);

            var again = await scope.ServiceProvider.GetRequiredService<SuiteService>().AddAsync(slug, new SuiteAddition(Procurement, Essentials, null, null, "Twice"), Cancel);
            Assert.Equal(TenancyErrors.SuiteInstalled, again.Error.Code);
        }
    }

    [Fact]
    [Proves("TF-244 AC4", "TF-244 AC5", "ADR-0059")]
    public async Task RemovingASuiteTakesOnlyItsEntitlementsAwayWithTheHistoryAndAddingItAgainGivesThemBack()
    {
        var operatorId = Ids.New();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(operatorId));
        var suffix = $"{Ids.New():N}"[..8];
        var slug = await ActiveTenantAsync(provider, $"remove-{suffix}", null, null, [new SuiteChoice(Procurement, Essentials)]);
        int foundationRows;
        await using (var scope = provider.CreateAsyncScope())
        {
            var record = await scope.ServiceProvider.GetRequiredService<TenantRegistry>().FindAsync(slug, Cancel);
            Assert.Equal([Procurement], record!.InstalledSuites);
            foundationRows = (await FoundationRowsAsync(scope, slug)).Count;

            // TF-244 AC5: the suite's own plan switches its module to read only, and the store's copy is behind until published.
            var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
            Assert.True((await plans.SaveAsync($"reading-{suffix}", new PlanInput("Reading", 5, [new PlanModuleInput("purchasing", EntitlementStatus.ReadOnly, null)], [], Procurement), Cancel)).IsSuccess);
            var switched = await scope.ServiceProvider.GetRequiredService<EntitlementService>().ApplyAsync(slug, new PlanApplication($"reading-{suffix}", null, null, "Read only while the contract is renewed"), Cancel);
            Assert.True(switched.IsSuccess, switched.IsFailure ? switched.Error.Message : null);
            var changed = Assert.Single(switched.Value.Changed);
            Assert.Equal(("purchasing", EntitlementStatus.ReadOnly), (changed.Code, changed.After!.Status));
            var document = await scope.ServiceProvider.GetRequiredService<EntitlementService>().DocumentAsync(slug, Cancel);
            Assert.Equal((EntitlementStatus.ReadOnly, 5), (document!.Modules.Single(m => m.Code == "purchasing").Status, document.Modules.Single(m => m.Code == "purchasing").GraceDays));
            Assert.Equal(foundationRows, (await FoundationRowsAsync(scope, slug)).Count);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var suites = scope.ServiceProvider.GetRequiredService<SuiteService>();
            var removed = await suites.RemoveAsync(slug, Procurement, "The contract ended", Cancel);

            Assert.True(removed.IsSuccess, removed.IsFailure ? removed.Error.Message : null);
            Assert.Equal(["purchasing"], removed.Value.Entitlements.Removed.Select(r => r.Code));
            Assert.Equal(JobKind.Update, removed.Value.Job.Kind);
            Assert.Contains(removed.Value.Entitlements.Warnings, w => w.Contains("its data stays", StringComparison.Ordinal));
            var twice = await suites.RemoveAsync(slug, Procurement, "Again", Cancel);
            Assert.Equal(TenancyErrors.SuiteNotInstalled, twice.Error.Code);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.Empty((await scope.ServiceProvider.GetRequiredService<TenantRegistry>().FindAsync(slug, Cancel))!.InstalledSuites);
            Assert.Equal(foundationRows, (await FoundationRowsAsync(scope, slug)).Count);
            var document = await scope.ServiceProvider.GetRequiredService<EntitlementService>().DocumentAsync(slug, Cancel);
            Assert.DoesNotContain(document!.Modules, m => m.Code == "purchasing");
            Assert.Empty(document.Suites!);

            var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var tenant = await context.Tenants.AsNoTracking().SingleAsync(t => t.Slug == slug, Cancel);
            var history = await context.EntitlementHistory.AsNoTracking().Where(c => c.TenantId == tenant.Id && c.Reason == "The contract ended").ToListAsync(Cancel);
            Assert.Equal([("module", "purchasing"), ("plan", Procurement)], history.Select(c => (c.Kind, c.Code)).Order());
            Assert.All(history, c => Assert.Null(c.After));
            var audit = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().SingleAsync(e => e.EventType == TenancyAuditEvents.SuiteRemoved && e.Target == slug, Cancel);
            Assert.Equal((operatorId, "The contract ended"), (audit.ActorId, audit.Reason));

            // A suite plan applies only to a tenant that has the suite.
            var refused = await scope.ServiceProvider.GetRequiredService<EntitlementService>().ApplyAsync(slug, new PlanApplication(Essentials, null, null, "Without the suite"), Cancel);
            Assert.Equal(TenancyErrors.SuiteNotInstalled, refused.Error.Code);
        }

        await CloseLatestJobAsync(provider, slug, time);
        await using (var scope = provider.CreateAsyncScope())
        {
            var again = await scope.ServiceProvider.GetRequiredService<SuiteService>().AddAsync(slug, new SuiteAddition(Procurement, Essentials, null, null, "Renewed"), Cancel);
            Assert.True(again.IsSuccess, again.IsFailure ? again.Error.Message : null);
            Assert.Equal(["purchasing"], again.Value.Entitlements.Added.Select(a => a.Code));
            Assert.Equal([Procurement], (await scope.ServiceProvider.GetRequiredService<TenantRegistry>().FindAsync(slug, Cancel))!.InstalledSuites);
        }
    }

    [Fact]
    [Proves("TF-244 AC7", "ADR-0059")]
    public async Task APlanNamingAModuleOrFeatureOfAnotherGroupIsRefusedWhenSavedAndWhenApplied()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(Ids.New()));
        var suffix = $"{Ids.New():N}"[..8];
        await using var scope = provider.CreateAsyncScope();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();

        var foundationFeature = await plans.SaveAsync($"mixed-{suffix}", new PlanInput("Mixed", 30, [new PlanModuleInput("purchasing", EntitlementStatus.Enabled, null)], ["authorization.role_transfer"], Procurement), Cancel);
        Assert.Equal(TenancyErrors.PlanOtherGroup, foundationFeature.Error.Code);
        Assert.Contains("'authorization.role_transfer' is a feature of the Foundation (foundation), not of the suite 'procurement'", foundationFeature.Error.Message, StringComparison.Ordinal);

        var suiteModule = await plans.SaveAsync($"foundation-{suffix}", new PlanInput("Foundation with a suite", 7, [new PlanModuleInput("purchasing", EntitlementStatus.Enabled, null)], []), Cancel);
        Assert.Equal(TenancyErrors.PlanOtherGroup, suiteModule.Error.Code);
        Assert.Contains("'purchasing' is a module of the suite 'procurement', not of the Foundation (foundation)", suiteModule.Error.Message, StringComparison.Ordinal);

        var unknownSuite = await plans.SaveAsync($"school-{suffix}", new PlanInput("School", 7, [], [], "school"), Cancel);
        Assert.Equal(TenancyErrors.PlanSuiteUnknown, unknownSuite.Error.Code);

        Assert.True((await plans.SaveAsync($"own-{suffix}", new PlanInput("Own", 30, [new PlanModuleInput("purchasing", EntitlementStatus.Enabled, null)], [], Procurement), Cancel)).IsSuccess);
        var moved = await plans.SaveAsync($"own-{suffix}", new PlanInput("Own", 30, [], []), Cancel);
        Assert.True(moved.IsSuccess, "A plan saved without its group keeps the group it has.");
        Assert.Equal(Procurement, moved.Value.Suite);
        var otherGroup = await plans.SaveAsync($"own-{suffix}", new PlanInput("Own", 30, [], [], SuiteName.Foundation), Cancel);
        Assert.Equal(TenancyErrors.PlanSuiteFixed, otherGroup.Error.Code);

        // A plan written before this build, which names another group's module, is refused when it is applied.
        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        context.Plans.Add(new Plan($"old-{suffix}", "Old", 7, [new PlanModule("purchasing", EntitlementStatus.Enabled, SubscriptionType.Platform, null)], []));
        await context.SaveChangesAsync(Cancel);
        var slug = await ActiveTenantAsync(provider, $"old-{suffix}", null, null);
        var applied = await scope.ServiceProvider.GetRequiredService<EntitlementService>().ApplyAsync(slug, new PlanApplication($"old-{suffix}", null, null, "Old plan"), Cancel);
        Assert.Equal(TenancyErrors.PlanOtherGroup, applied.Error.Code);
    }

    [Fact]
    [Proves("TF-244 AC7", "ADR-0059")]
    public async Task APlanOfOneSuiteCannotNameAnotherSuitesModule()
    {
        var catalogue = new UnitCatalogue(new LicensingCatalogue(
            [new LicensedModuleEntry("purchasing", "purchasing", SubscriptionType.Platform, []) { Suite = Procurement }, new LicensedModuleEntry("timetables", "timetables", SubscriptionType.Platform, []) { Suite = "school" }],
            [new FeatureEntry("authorization.role_transfer", "authorization", null, "Role export and import")],
            ["audit", "authorization"]));
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(Ids.New()), services => services.AddSingleton(catalogue));
        await using var scope = provider.CreateAsyncScope();

        var saved = await scope.ServiceProvider.GetRequiredService<PlanService>().SaveAsync($"both-{Ids.New():N}"[..20], new PlanInput("Both", 30, [new PlanModuleInput("purchasing", EntitlementStatus.Enabled, null), new PlanModuleInput("timetables", EntitlementStatus.Enabled, null)], [], Procurement), Cancel);

        Assert.Equal(TenancyErrors.PlanOtherGroup, saved.Error.Code);
        var problem = Assert.Single(saved.Error.Fields!);
        Assert.Equal("'timetables' is a module of the suite 'school', not of the suite 'procurement': a plan names only its own group's modules and features.", problem.Message);
    }

    [Fact]
    [Proves("TF-244 AC1", "ADR-0059")]
    public async Task ATenantCreatedWithASuiteHoldsItsPlanAndAnUnknownSuiteRefusesTheTenant()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(Ids.New()));
        var suffix = $"{Ids.New():N}"[..8];
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();

        var created = await registry.CreateAsync(new NewTenant($"with-{suffix}", "With", [], $"admin@with-{suffix}.test", null, SizeClass.Small, null, null, Suites: [new SuiteChoice(Procurement, Essentials), new SuiteChoice("Procurement")], Reason: " Bought with the suite "), Cancel);
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
        Assert.Equal([Procurement], created.Value.InstalledSuites);
        var suites = await scope.ServiceProvider.GetRequiredService<SuiteService>().ListAsync($"with-{suffix}", Cancel);
        var suite = Assert.Single(suites.Value);
        Assert.Equal((Procurement, Essentials, 30), (suite.Suite, suite.Plan, suite.GraceDays));

        // The operator's reason is the trail's and the history's, for the tenant and for its suite's plan.
        var registered = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().SingleAsync(e => e.EventType == TenancyAuditEvents.TenantCreated && e.Target == $"with-{suffix}", Cancel);
        Assert.Equal("Bought with the suite", registered.Reason);
        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var history = await context.EntitlementHistory.AsNoTracking().Where(c => c.TenantSlug == $"with-{suffix}").ToListAsync(Cancel);
        Assert.All(history, c => Assert.Equal("Bought with the suite", c.Reason));
        Assert.Contains(history, c => (c.Kind, c.Code) == ("plan", Procurement));
        Assert.Contains(history, c => (c.Kind, c.Code) == ("module", "purchasing"));

        var unknown = await registry.CreateAsync(new NewTenant($"school-{suffix}", "School", [], $"admin@school-{suffix}.test", null, SizeClass.Small, null, null, Suites: [new SuiteChoice("school")]), Cancel);
        Assert.Equal(TenancyErrors.SuiteUnknown, unknown.Error.Code);
        Assert.Null(await registry.FindAsync($"school-{suffix}", Cancel));

        var otherPlan = await registry.CreateAsync(new NewTenant($"wrong-{suffix}", "Wrong", [], $"admin@wrong-{suffix}.test", null, SizeClass.Small, null, null, Suites: [new SuiteChoice(Procurement, "standard")]), Cancel);
        Assert.Equal(TenancyErrors.PlanOtherGroup, otherPlan.Error.Code);
        Assert.Null(await registry.FindAsync($"wrong-{suffix}", Cancel));
    }

    [Fact]
    [Proves("TF-244 AC3", "ADR-0059")]
    public async Task ASuiteWithOnePlanIsAddedUnderItAndASuiteWithSeveralAsksWhich()
    {
        // A suite of its own, so the plans other tests save for the eProcurement Suite in this database do not count.
        var suite = $"garden{Ids.New():N}"[..14];
        var catalogue = new UnitCatalogue(new LicensingCatalogue([new LicensedModuleEntry($"{suite}-beds", "beds", SubscriptionType.Platform, []) { Suite = suite }], [], ["audit"]));
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(Ids.New()), services => services.AddSingleton(catalogue));
        await using var scope = provider.CreateAsyncScope();
        var entitlements = scope.ServiceProvider.GetRequiredService<EntitlementService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();

        Assert.Equal(TenancyErrors.SuitePlanRequired, (await entitlements.SuitePlanAsync(suite, null, Cancel)).Error.Code);
        Assert.True((await plans.SaveAsync($"{suite}-one", new PlanInput("One", 30, [new PlanModuleInput($"{suite}-beds", EntitlementStatus.Enabled, null)], [], suite), Cancel)).IsSuccess);
        Assert.Equal($"{suite}-one", (await entitlements.SuitePlanAsync(suite, null, Cancel)).Value);
        Assert.True((await plans.SaveAsync($"{suite}-two", new PlanInput("Two", 30, [], [], suite), Cancel)).IsSuccess);

        var several = await entitlements.SuitePlanAsync(suite, null, Cancel);
        Assert.Equal(TenancyErrors.SuitePlanRequired, several.Error.Code);
        Assert.Contains($"{suite}-one, {suite}-two", several.Error.Message, StringComparison.Ordinal);
        Assert.Equal($"{suite}-two", (await entitlements.SuitePlanAsync(suite, $" {suite}-TWO ", Cancel)).Value);
        Assert.Equal(TenancyErrors.SuiteUnknown, (await entitlements.SuitePlanAsync(SuiteName.Foundation, null, Cancel)).Error.Code);
    }

    /// <summary>A tenant made active, as the fleet leaves it after provisioning, with its provisioning job closed.</summary>
    private static async Task<string> ActiveTenantAsync(ServiceProvider provider, string slug, string? plan, IReadOnlyList<string>? features, IReadOnlyList<SuiteChoice>? suites = null)
    {
        await using var scope = provider.CreateAsyncScope();
        if (plan is not null)
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<PlanService>().SaveAsync(plan, new PlanInput("Foundation with role transfer", 7, [], features ?? []), Cancel)).IsSuccess);
        }

        var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
        var created = await registry.CreateAsync(new NewTenant(slug, slug, [], $"admin@{slug}.test", null, SizeClass.Small, null, plan, Suites: suites), Cancel);
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
        Assert.True((await registry.SetStatusAsync(slug, TenantStatus.Active, Cancel)).IsSuccess);
        return slug;
    }

    private static async Task<IReadOnlyList<TenantEntitlement>> FoundationRowsAsync(AsyncServiceScope scope, string slug)
    {
        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = await context.Tenants.AsNoTracking().SingleAsync(t => t.Slug == slug, Cancel);
        return await scope.ServiceProvider.GetRequiredService<EntitlementService>().RowsAsync(tenant.Id, asTracking: false, Cancel, SuiteName.Foundation);
    }

    /// <summary>The fleet finishing the update job, so the next change of the tenant's suites may ask for its own.</summary>
    private static async Task CloseLatestJobAsync(ServiceProvider provider, string slug, TimeProvider time)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
        var job = await context.ProvisioningJobs.Where(j => j.TenantSlug == slug).OrderByDescending(j => j.RequestedAt).ThenByDescending(j => j.Id).FirstAsync(Cancel);
        job.Start(time.GetUtcNow());
        job.Complete(time.GetUtcNow(), "test");
        await context.SaveChangesAsync(Cancel);
    }
}
