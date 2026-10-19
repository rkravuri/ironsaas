using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Fleet.Services.Steps;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Fleet.Tests;

/// <summary>
/// ADR-0034 against a real store: the control plane writes a tenant's entitlements to <c>tenants/&lt;slug&gt;/entitlements</c>
/// after every committed change, the fleet writes them again when the store refused, the unit's token may read them and
/// nobody else's, and an update widens the policy of a unit made before the path existed.
/// </summary>
[Collection(FleetCollection.Name)]
public class EntitlementStoreTests(FleetFixture fixture)
{
    [Fact]
    public async Task AnAppliedPlanReachesTheStoreAndTheUnitsTokenReadsItsOwnEntitlementsOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), new RecordingRuntime());
        var slug = await ProvisionAsync(provider, "store");
        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        Assert.Equal(1, (await DocumentAsync(root, slug)).Revision);

        await ApplyAsync(provider, slug, PlanSeeder.Enterprise);

        var document = await DocumentAsync(root, slug);
        Assert.Equal((2, "enterprise", 30), (document.Revision, document.Plan, document.GraceDays));
        Assert.Equal("authorization.role_transfer", Assert.Single(document.Features).Code);
        Assert.Equal(2, await PublishedAsync(provider, slug));

        var token = (await root.ReadAsync($"tenants/{slug}/fleet", ct))![SecretsStep.UnitTokenKey];
        using var unit = new OpenBaoSecretStore(fixture.StoreOptions(token));
        Assert.NotNull(await unit.ReadAsync($"tenants/{slug}/entitlements", ct));
        var other = await ProvisionAsync(provider, "other");
        await Assert.ThrowsAsync<SecretStoreException>(() => unit.ReadAsync($"tenants/{other}/entitlements", ct));
    }

    [Fact]
    public async Task AWriteTheStoreRefusedIsWrittenByTheFleetsReconcile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), new RecordingRuntime());
        var slug = await ProvisionAsync(provider, "recon");

        // The store refuses while the plan is applied: the change commits, and the store's copy stays behind.
        await using (var refusing = fixture.BuildServices(FleetFixture.Clock(), new RecordingRuntime(), services => services.AddSingleton<ISecretStore, RefusingStore>()))
        {
            await ApplyAsync(refusing, slug, PlanSeeder.Enterprise);
        }

        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        Assert.Equal(1, (await DocumentAsync(root, slug)).Revision);
        Assert.Equal(1, await PublishedAsync(provider, slug));

        var reconciler = ActivatorUtilities.CreateInstance<EntitlementReconciler>(provider);
        Assert.True(await reconciler.ReconcileAsync(ct) >= 1);

        Assert.Equal(2, (await DocumentAsync(root, slug)).Revision);
        Assert.Equal(2, await PublishedAsync(provider, slug));
        Assert.Equal(0, await reconciler.ReconcileAsync(ct));
        Assert.Equal(0, await CountBehindAsync(provider));
    }

    [Fact]
    public async Task AnUpdateWidensThePolicyOfAUnitMadeBeforeTheEntitlementsAndWritesThemAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), new RecordingRuntime());
        var slug = await ProvisionAsync(provider, "widen");
        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        var token = (await root.ReadAsync($"tenants/{slug}/fleet", ct))![SecretsStep.UnitTokenKey];

        // As the secrets step wrote it before A4: the unit secret alone, and the entitlements lost with a store.
        var admin = provider.GetRequiredService<ISecretStoreAdmin>();
        await admin.CreateReadPolicyAsync($"tenant-{slug}", [$"tenants/{slug}/unit"], ct);
        using var unit = new OpenBaoSecretStore(fixture.StoreOptions(token));
        await Assert.ThrowsAsync<SecretStoreException>(() => unit.ReadAsync($"tenants/{slug}/entitlements", ct));

        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<TenantRegistry>().RequestUpdateAsync(slug, ct)).IsSuccess);
        }

        Assert.True(await new ProvisioningWorkerHarness(provider).RunNextAsync());

        // The same token, which the update kept, now reads the entitlements.
        Assert.Equal(token, (await root.ReadAsync($"tenants/{slug}/fleet", ct))![SecretsStep.UnitTokenKey]);
        var secret = await unit.ReadAsync($"tenants/{slug}/entitlements", ct);
        Assert.Equal(1, EntitlementsDocument.Parse(secret![EntitlementsDocument.DocumentKey]).Revision);
    }

    private static async Task<string> ProvisionAsync(ServiceProvider provider, string prefix)
    {
        var slug = $"{prefix}-{Ids.New():N}"[..20];
        await using (var scope = provider.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
            var created = await registry.CreateAsync(new NewTenant(slug, prefix, [], $"admin@{prefix}.test", "Admin", SizeClass.Small, null, null), TestContext.Current.CancellationToken);
            Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
            Assert.True((await registry.RequestProvisioningAsync(slug, TestContext.Current.CancellationToken)).IsSuccess);
        }

        Assert.True(await new ProvisioningWorkerHarness(provider).RunNextAsync());
        return slug;
    }

    private static async Task ApplyAsync(ServiceProvider provider, string slug, string plan)
    {
        await using var scope = provider.CreateAsyncScope();
        var applied = await scope.ServiceProvider.GetRequiredService<EntitlementService>().ApplyAsync(slug, new PlanApplication(plan, null, null, $"Plan {plan} applied"), TestContext.Current.CancellationToken);
        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);
        await scope.ServiceProvider.GetRequiredService<ITenantEntitlements>().PublishAsync(slug, TestContext.Current.CancellationToken);
    }

    private static async Task<EntitlementsDocument> DocumentAsync(ISecretStore store, string slug)
    {
        var secret = await store.ReadAsync(EntitlementsDocument.PathOf(slug), TestContext.Current.CancellationToken);
        Assert.NotNull(secret);
        return EntitlementsDocument.Parse(secret[EntitlementsDocument.DocumentKey]);
    }

    private static async Task<int> PublishedAsync(ServiceProvider provider, string slug)
    {
        await using var scope = provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().Tenants.AsNoTracking().SingleAsync(t => t.Slug == slug, TestContext.Current.CancellationToken)).PublishedRevision;
    }

    private static async Task<int> CountBehindAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().Tenants.CountAsync(t => t.PublishedRevision < t.EntitlementsRevision, TestContext.Current.CancellationToken);
    }

    /// <summary>A store that refuses every write, as one that is sealed or down does.</summary>
    private sealed class RefusingStore : ISecretStore
    {
        public Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken) => throw new SecretStoreException("The store is sealed.");

        public Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) => throw new SecretStoreException("The store is sealed.");

        public Task<bool> IsReachableAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
