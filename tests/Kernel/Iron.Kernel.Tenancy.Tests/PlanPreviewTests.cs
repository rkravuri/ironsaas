using Iron.Kernel.Api.Modules;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Tenancy.Tests;

/// <summary>
/// The preview and the apply with a licensed module, which no module of this build has yet: a catalogue with a per-person
/// module stands in. A plan that removes it warns that its data stays and is hidden (TF-042 AC2); seats come from the plan.
/// </summary>
[Collection(TenancyCollection.Name)]
public class PlanPreviewTests(TenancyFixture fixture)
{
    private static readonly UnitCatalogue Catalogue = new(new LicensingCatalogue(
        [new LicensedModuleEntry("sourcing", "sourcing", SubscriptionType.PerPerson, ["sourcing.auctions"]), new LicensedModuleEntry("contracts", "contracts", SubscriptionType.Platform, [])],
        [new FeatureEntry("sourcing.auctions", "sourcing", "sourcing", "Auctions"), new FeatureEntry("authorization.role_transfer", "authz", null, "Role export and import")],
        ["authz", "identity"]));

    [Fact]
    [Proves("TF-042 AC2", "ADR-0034")]
    public async Task RemovingAModuleWarnsThatItsDataStaysAndChangingItsSeatsIsAChange()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(Ids.New()), services => services.AddSingleton(Catalogue));
        var suffix = $"{Ids.New():N}"[..8];
        var slug = $"preview-{suffix}";
        await using (var scope = provider.CreateAsyncScope())
        {
            var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
            Assert.True((await plans.SaveAsync($"buyer-{suffix}", new PlanInput("Buyer", 10, [new PlanModuleInput("sourcing", EntitlementStatus.Enabled, 25), new PlanModuleInput("contracts", EntitlementStatus.Enabled, 99)], ["sourcing.auctions"]), TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await plans.SaveAsync($"small-{suffix}", new PlanInput("Small buyer", 10, [new PlanModuleInput("sourcing", EntitlementStatus.ReadOnly, 5)], []), TestContext.Current.CancellationToken)).IsSuccess);
            var noSeats = await plans.SaveAsync($"broken-{suffix}", new PlanInput("Broken", 10, [new PlanModuleInput("sourcing", EntitlementStatus.Enabled, null)], []), TestContext.Current.CancellationToken);
            Assert.Equal("tenancy.plan.user_limit", noSeats.Error.Code);

            var created = await scope.ServiceProvider.GetRequiredService<TenantRegistry>().CreateAsync(new NewTenant(slug, "Preview", [], $"admin@{slug}.test", null, SizeClass.Small, null, $"buyer-{suffix}"), TestContext.Current.CancellationToken);
            Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var tenant = await context.Tenants.AsNoTracking().SingleAsync(t => t.Slug == slug, TestContext.Current.CancellationToken);
            var rows = await context.Entitlements.AsNoTracking().Where(e => e.TenantId == tenant.Id).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["contracts", "sourcing", "sourcing.auctions"], rows.Select(r => r.Code).Order(StringComparer.Ordinal));
            var sourcing = rows.Single(r => r.Code == "sourcing");
            Assert.Equal((SubscriptionType.PerPerson, 25, new DateOnly(2026, 10, 20)), (sourcing.SubscriptionType!.Value, sourcing.UserLimit!.Value, sourcing.StartsOn));
            Assert.Null(rows.Single(r => r.Code == "contracts").UserLimit);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var preview = await scope.ServiceProvider.GetRequiredService<EntitlementService>().PreviewAsync(slug, new PlanApplication($"small-{suffix}", null, null, "test"), TestContext.Current.CancellationToken);

            Assert.True(preview.IsSuccess, preview.IsFailure ? preview.Error.Message : null);
            var removed = preview.Value.Removed.Select(r => r.Code).ToList();
            Assert.Equal(["contracts", "sourcing.auctions"], removed);
            var changed = Assert.Single(preview.Value.Changed);
            Assert.Equal(("sourcing", EntitlementStatus.ReadOnly, 5), (changed.Code, changed.After!.Status, changed.After.UserLimit!.Value));
            Assert.Equal(new DateOnly(2026, 10, 20), changed.After.StartsOn);
            Assert.Contains("The module 'contracts' leaves the subscription: its data stays in the tenant's database and is hidden until the module is licensed again.", preview.Value.Warnings);
            Assert.Contains("The module 'sourcing' becomes read only: its data can be seen and not changed.", preview.Value.Warnings);
        }
    }
}
