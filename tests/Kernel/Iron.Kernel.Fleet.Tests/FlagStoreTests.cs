using System.Text.Json;
using Iron.Kernel.Flags.Contracts;
using Iron.Kernel.Fleet.Services.Steps;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Fleet.Tests;

/// <summary>
/// ADR-0035 against a real store: a global kill switch is one write to <c>platform/flags</c>, a tenant's rule goes to
/// <c>tenants/&lt;slug&gt;/flags</c> with the tenant's region and plan, the unit's token reads its own path and the shared one
/// and no other tenant's, and what the store refused is written by the fleet's reconcile.
/// </summary>
[Collection(FleetCollection.Name)]
public class FlagStoreTests(FleetFixture fixture)
{
    [Fact]
    public async Task AGlobalKillSwitchIsOneWriteAndATenantRuleGoesToItsOwnPathTheUnitsTokenReads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), new RecordingRuntime());
        var slug = await ProvisionAsync(provider, "flagged");
        var other = await ProvisionAsync(provider, "neighbour");

        await using (var scope = provider.CreateAsyncScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<FlagRuleService>();
            Assert.True((await rules.SetKillSwitchAsync("imports", true, "Imports wait for a fix.", null, ct)).IsSuccess);
            Assert.True((await rules.ReplaceAsync(ExampleFlags.Tone.Key, [new FlagRuleInput(FlagScope.Tenant, null, slug, null, null, null, JsonSerializer.SerializeToElement("warm"), null)], null, null, "rules", ct)).IsSuccess);
            Assert.True(await scope.ServiceProvider.GetRequiredService<FlagPublisher>().ReconcileAsync(ct) >= 2);
        }

        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        var platform = await DocumentAsync(root, FlagRulesDocument.PlatformPath);
        var killed = Assert.Single(platform.Rules, r => r.Flag == "kill.imports");
        Assert.Equal((FlagScope.Global, "Imports wait for a fix."), (killed.Scope, killed.Reason));
        var tenant = await DocumentAsync(root, FlagRulesDocument.TenantPath(slug));
        Assert.Equal("warm", Assert.Single(tenant.Rules).Value.GetString());
        Assert.Equal(("local", "standard"), (tenant.Attributes!["region"], tenant.Attributes["plan"]));

        var token = (await root.ReadAsync($"tenants/{slug}/fleet", ct))![SecretsStep.UnitTokenKey];
        using var unit = new OpenBaoSecretStore(fixture.StoreOptions(token));
        Assert.NotNull(await unit.ReadAsync(FlagRulesDocument.PlatformPath, ct));
        Assert.NotNull(await unit.ReadAsync(FlagRulesDocument.TenantPath(slug), ct));
        await Assert.ThrowsAsync<SecretStoreException>(() => unit.ReadAsync(FlagRulesDocument.TenantPath(other), ct));
        await Assert.ThrowsAsync<SecretStoreException>(() => unit.WriteAsync(FlagRulesDocument.PlatformPath, new Dictionary<string, string> { ["document"] = "{}" }, ct));

        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<FlagRuleService>().SetKillSwitchAsync("imports", false, "The fix is out.", null, ct)).IsSuccess);
            await scope.ServiceProvider.GetRequiredService<FlagPublisher>().ReconcileAsync(ct);
        }

        Assert.DoesNotContain((await DocumentAsync(root, FlagRulesDocument.PlatformPath)).Rules, r => r.Flag == "kill.imports");
    }

    [Fact]
    public async Task WhatTheStoreRefusedIsWrittenByTheFleetsReconcile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), new RecordingRuntime());
        var slug = await ProvisionAsync(provider, "refused");

        await using (var refusing = fixture.BuildServices(FleetFixture.Clock(), new RecordingRuntime(), services => services.AddSingleton<ISecretStore, RefusingStore>()))
        {
            await using var scope = refusing.CreateAsyncScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<FlagRuleService>().ReplaceAsync(ExampleFlags.Release.Key, [new FlagRuleInput(FlagScope.Tenant, null, slug, null, null, null, JsonSerializer.SerializeToElement(true), null)], null, null, "rules", ct)).IsSuccess);
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<FlagPublisher>().ReconcileAsync(ct));
        }

        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        Assert.Empty((await DocumentAsync(root, FlagRulesDocument.TenantPath(slug))).Rules);

        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<FlagPublisher>().ReconcileAsync(ct) >= 1);
        }

        Assert.True(Assert.Single((await DocumentAsync(root, FlagRulesDocument.TenantPath(slug))).Rules).Value.GetBoolean());
    }

    private static async Task<string> ProvisionAsync(ServiceProvider provider, string prefix)
    {
        var slug = $"{prefix}-{Ids.New():N}"[..20];
        await using (var scope = provider.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
            Assert.True((await registry.CreateAsync(new NewTenant(slug, prefix, [], $"admin@{prefix}.test", "Admin", SizeClass.Small, null, null), TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await registry.RequestProvisioningAsync(slug, TestContext.Current.CancellationToken)).IsSuccess);
        }

        Assert.True(await new ProvisioningWorkerHarness(provider).RunNextAsync());
        return slug;
    }

    private static async Task<FlagRulesDocument> DocumentAsync(ISecretStore store, string path)
    {
        var secret = await store.ReadAsync(path, TestContext.Current.CancellationToken);
        Assert.NotNull(secret);
        return FlagRulesDocument.Parse(secret[FlagRulesDocument.DocumentKey]);
    }

    private sealed class RefusingStore : ISecretStore
    {
        public Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken) => throw new SecretStoreException("The store is sealed.");

        public Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) => throw new SecretStoreException("The store is sealed.");

        public Task<bool> IsReachableAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
