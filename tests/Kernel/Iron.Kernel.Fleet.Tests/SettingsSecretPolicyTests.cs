using Iron.Kernel.Fleet.Services.Steps;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Fleet.Tests;

/// <summary>
/// ADR-0036 against a real store: the secrets behind a tenant's secret-reference settings live below
/// <c>tenants/&lt;slug&gt;/settings/</c>, and the unit's token reads everything there and nothing of another tenant's.
/// </summary>
[Collection(FleetCollection.Name)]
public class SettingsSecretPolicyTests(FleetFixture fixture)
{
    [Fact]
    public async Task TheUnitsTokenReadsItsOwnSettingsSecretsAndNoOtherTenants()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), new RecordingRuntime());
        var slug = await ProvisionAsync(provider, "secrets");
        var other = await ProvisionAsync(provider, "neighbour");

        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        await root.WriteAsync($"tenants/{slug}/settings/mail", new Dictionary<string, string> { [SecretReference.ValueKey] = "the mail password" }, ct);
        await root.WriteAsync($"tenants/{other}/settings/mail", new Dictionary<string, string> { [SecretReference.ValueKey] = "another tenant's password" }, ct);

        var token = (await root.ReadAsync($"tenants/{slug}/fleet", ct))![SecretsStep.UnitTokenKey];
        using var unit = new OpenBaoSecretStore(fixture.StoreOptions(token));
        Assert.Equal("the mail password", (await unit.ReadAsync($"tenants/{slug}/settings/mail", ct))![SecretReference.ValueKey]);
        await Assert.ThrowsAsync<SecretStoreException>(() => unit.ReadAsync($"tenants/{other}/settings/mail", ct));
        await Assert.ThrowsAsync<SecretStoreException>(() => unit.WriteAsync($"tenants/{slug}/settings/mail", new Dictionary<string, string> { [SecretReference.ValueKey] = "changed" }, ct));
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
}
