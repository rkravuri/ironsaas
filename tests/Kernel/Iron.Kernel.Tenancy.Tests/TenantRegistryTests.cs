using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Fleet.Data;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Tenancy.Tests;

/// <summary>Run without the database on purpose, so their messages show even when the fixture cannot migrate.</summary>
public class ModelTests
{
    [Fact]
    [Proves("ADR-0012")]
    public void TheTenancyModelHasNoChangesWithoutAMigration()
    {
        using var context = new TenancyDbContext(DesignTime.OptionsFor<TenancyDbContext>());

        Assert.False(context.Database.HasPendingModelChanges(), "The Tenancy model has changes without a migration. Run: make ef-add MODULE=Tenancy NAME=<WhatChanged>.");
    }

    [Fact]
    [Proves("ADR-0012")]
    public void TheFleetModelHasNoChangesWithoutAMigration()
    {
        using var context = new FleetDbContext(DesignTime.OptionsFor<FleetDbContext>());

        Assert.False(context.Database.HasPendingModelChanges(), "The Fleet model has changes without a migration. Run: make ef-add MODULE=Fleet NAME=<WhatChanged>.");
    }

    [Theory]
    [Untraced("How a hostname is normalised (case, port) and which labels are valid is stated by no criterion or ADR; ADR-0017 only makes hostnames unique.")]
    [InlineData("acme.localhost", true)]
    [InlineData("Acme.Localhost:443", true)]
    [InlineData("acme", true)]
    [InlineData("a-b.c-d.example", true)]
    [InlineData("-acme.localhost", false)]
    [InlineData("acme..localhost", false)]
    [InlineData("acme_1.localhost", false)]
    [InlineData("", false)]
    public void HostnamesAreNormalisedAndValidated(string hostname, bool valid)
    {
        Assert.Equal(valid, TenantHostname.IsValid(TenantHostname.Normalise(hostname)));
    }
}

[Collection(TenancyCollection.Name)]
public class TenantRegistryTests(TenancyFixture fixture)
{
    [Fact]
    [Proves("ADR-0008")]
    public async Task TheMigratedSchemasPassTheSchemaCheck()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));

        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["schema-check"], TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("TF-001 AC2", "ADR-0017")]
    public async Task CreatesATenantWithADefaultHostnameAndRefusesDuplicates()
    {
        var operatorId = Ids.New();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(operatorId));
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
        var slug = Slug("acme");

        var created = await registry.CreateAsync(new NewTenant(slug, "Acme Ltd", [], "Admin@Acme.test", "Acme Admin", SizeClass.Medium, null, null), TestContext.Current.CancellationToken);

        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
        var tenant = created.Value;
        Assert.Equal(TenantStatus.Provisioning, tenant.Status);
        Assert.Equal([$"{slug}.localhost"], tenant.Hostnames);
        Assert.Equal("admin@acme.test", tenant.AdministratorLoginId);
        Assert.Equal(slug, tenant.DatabaseName);
        Assert.Equal(slug, tenant.StorageContainer);
        Assert.Equal("local", tenant.Region);
        Assert.Equal(SizeClass.Medium, tenant.SizeClass);

        var sameSlug = await registry.CreateAsync(new NewTenant(slug, "Acme again", ["other.localhost"], "x@acme.test", null, SizeClass.Small, null, null), TestContext.Current.CancellationToken);
        Assert.Equal(TenancyErrors.SlugTaken, sameSlug.Error.Code);

        var sameHostname = await registry.CreateAsync(new NewTenant(Slug("bravo"), "Bravo", [$"{slug}.localhost"], "x@bravo.test", null, SizeClass.Small, null, null), TestContext.Current.CancellationToken);
        Assert.Equal(TenancyErrors.HostnameTaken, sameHostname.Error.Code);
        Assert.Contains($"{slug}.localhost", sameHostname.Error.Message, StringComparison.Ordinal);

        var found = await registry.FindAsync(slug, TestContext.Current.CancellationToken);
        Assert.NotNull(found);
        Assert.Equal(tenant.Id, found.Id);
    }

    [Fact]
    [Proves("ADR-0007")]
    public async Task RefusesABadSlugAndABadHostname()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();

        var badSlug = await registry.CreateAsync(new NewTenant("Acme Corp", "Acme", [], "a@b.test", null, SizeClass.Small, null, null), TestContext.Current.CancellationToken);
        Assert.True(badSlug.IsFailure);

        var badHostname = await registry.CreateAsync(new NewTenant(Slug("charlie"), "Charlie", ["under_score.localhost"], "a@b.test", null, SizeClass.Small, null, null), TestContext.Current.CancellationToken);
        Assert.Equal(TenancyErrors.HostnameInvalid, badHostname.Error.Code);
    }

    [Fact]
    [Proves("TF-002 AC3", "ADR-0017")]
    public async Task AProvisioningRequestCreatesOneOpenJobAndAnActiveTenantIsRefused()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(SystemPrincipal.SystemId));
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
        var jobs = scope.ServiceProvider.GetRequiredService<IProvisioningJobs>();
        var slug = Slug("delta");
        Assert.True((await registry.CreateAsync(new NewTenant(slug, "Delta", [], "a@delta.test", null, SizeClass.Small, null, null), TestContext.Current.CancellationToken)).IsSuccess);

        var requested = await registry.RequestProvisioningAsync(slug, TestContext.Current.CancellationToken);
        Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error.Message : null);
        Assert.Equal(ProvisioningStatus.Requested, requested.Value.Status);
        Assert.Equal(time.GetUtcNow(), requested.Value.RequestedAt);

        var again = await registry.RequestProvisioningAsync(slug, TestContext.Current.CancellationToken);
        Assert.Equal(FleetErrors.JobInProgress, again.Error.Code);

        var latest = await jobs.LatestAsync(slug, TestContext.Current.CancellationToken);
        Assert.Equal(requested.Value.Id, latest?.Id);

        Assert.True((await registry.SetStatusAsync(slug, TenantStatus.Active, TestContext.Current.CancellationToken)).IsSuccess);
        var active = await registry.RequestProvisioningAsync(slug, TestContext.Current.CancellationToken);
        Assert.Equal(TenancyErrors.AlreadyActive, active.Error.Code);
        Assert.NotNull((await registry.FindAsync(slug, TestContext.Current.CancellationToken))?.ActivatedAt);

        var unknown = await registry.RequestProvisioningAsync(Slug("nobody"), TestContext.Current.CancellationToken);
        Assert.Equal(TenancyErrors.TenantNotFound, unknown.Error.Code);
    }

    private static string Slug(string prefix) => $"{prefix}-{Ids.New():N}"[..24];
}
