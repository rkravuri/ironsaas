using Iron.Authorization.Contracts;
using Iron.Authorization.Data;
using Iron.Data.Commands;
using Iron.Data.Contexts;
using Iron.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Authorization.Tests;

[Collection(AuthorizationCollection.Name)]
public class SchemaAndSeedTests(AuthorizationFixture fixture)
{
    [Fact]
    public async Task TheMigratedSchemaPassesTheSchemaCheck()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), new RecordingAuditWriter());

        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["schema-check"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeedCreatesTheSystemRolesFromTheCatalogueAndRunsAgainWithoutDuplicates()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), new RecordingAuditWriter());
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var roles = await context.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SystemRoles.All.Order(StringComparer.Ordinal), roles.Select(r => r.Name));
        Assert.All(roles, role => Assert.True(role.IsSystem));
        var administrator = roles.Single(r => r.Name == SystemRoles.TenantAdministrator);
        Assert.Equal(AuthorizationPermissions.All.Select(p => p.Name).Order(StringComparer.Ordinal), administrator.Permissions);
        var readOnly = roles.Single(r => r.Name == SystemRoles.ReadOnly);
        Assert.Equal([AuthorizationPermissions.RolesRead.Name], readOnly.Permissions);
        Assert.Equal(PrincipalType.Service, roles.Single(r => r.Name == SystemRoles.ServiceDefault).PrincipalType);
    }
}

/// <summary>Runs without the database on purpose, so its message shows even when the fixture cannot migrate.</summary>
public class ModelTests
{
    [Fact]
    public void TheModelHasNoChangesWithoutAMigration()
    {
        using var context = new AuthorizationDbContext(DesignTime.OptionsFor<AuthorizationDbContext>());

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The Authorization model has changes without a migration. Run: make ef-add MODULE=Authorization NAME=<WhatChanged>, then commit the files under src/Iron.Authorization/Data/Migrations.");
    }
}
