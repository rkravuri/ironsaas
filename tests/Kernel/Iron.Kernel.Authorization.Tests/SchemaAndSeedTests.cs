using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Authorization.Tests;

[Collection(AuthorizationCollection.Name)]
public class SchemaAndSeedTests(AuthorizationFixture fixture)
{
    [Fact]
    [Proves("ADR-0008")]
    public async Task TheMigratedSchemaPassesTheSchemaCheck()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), new RecordingAuditWriter());

        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["schema-check"], TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0012", "ADR-0031")]
    public async Task SeedCreatesTheSystemRolesFromTheCatalogueAndRunsAgainWithoutDuplicates()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), new RecordingAuditWriter());
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var roles = await context.Roles.AsNoTracking().Where(r => r.IsSystem).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SystemRoles.All.Order(StringComparer.Ordinal), roles.OrderBy(r => r.Code).Select(r => r.Code));
        Assert.All(roles, role => Assert.True(role.IsSystem));
        var administrator = roles.Single(r => r.Code == SystemRoles.TenantAdministrator);
        Assert.Equal("Tenant administrator", administrator.Name);
        Assert.Equal(PrincipalKind.Any, administrator.PrincipalType);
        Assert.Equal(AuthorizationPermissions.All.Concat(new DutyProbeModule().Permissions).Select(p => p.Name).Order(StringComparer.Ordinal), administrator.Permissions);
        var readOnly = roles.Single(r => r.Code == SystemRoles.ReadOnly);
        Assert.Equal([AuthorizationPermissions.AssignmentsRead.Name, AuthorizationPermissions.RolesRead.Name, AuthorizationPermissions.SodRead.Name, AuthorizationPermissions.UnitsRead.Name, ProbeRecords.CratesRead.Name], readOnly.Permissions);
        Assert.Equal(PrincipalKind.Service, roles.Single(r => r.Code == SystemRoles.ServiceDefault).PrincipalType);
    }

    [Fact]
    [Proves("ADR-0030")]
    public async Task SeedCreatesOneRootUnitOfTheRootKindAndRunsAgainWithoutASecond()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), new RecordingAuditWriter());
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var root = await context.OrganisationUnits.AsNoTracking().SingleAsync(u => u.ParentId == null, TestContext.Current.CancellationToken);

        Assert.Equal(KernelOrganisationKinds.Root, root.Kind);
        Assert.Equal(0, root.Depth);
        Assert.Equal($"/{root.Id:N}/", root.Path);
        Assert.Equal(UnitStatus.Active, root.Status);
        var info = await scope.ServiceProvider.GetRequiredService<IOrganisationUnits>().RootAsync(TestContext.Current.CancellationToken);
        Assert.Equal(root.Id, info?.Id);
    }
}

/// <summary>Runs without the database on purpose, so its message shows even when the fixture cannot migrate.</summary>
public class ModelTests
{
    [Fact]
    [Proves("ADR-0012")]
    public void TheModelHasNoChangesWithoutAMigration()
    {
        using var context = new AuthorizationDbContext(DesignTime.OptionsFor<AuthorizationDbContext>());

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The Authorization model has changes without a migration. Run: make ef-add MODULE=Authorization NAME=<WhatChanged>, then commit the files under src/Kernel/Iron.Kernel.Authorization/Data/Migrations.");
    }
}
