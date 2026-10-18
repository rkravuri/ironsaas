using Iron.Authorization.Contracts;
using Iron.Data.Commands;
using Iron.Data.Contexts;
using Iron.Identity.Contracts;
using Iron.Identity.Data;
using Iron.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Identity.Tests;

/// <summary>Runs without the database on purpose, so its message shows even when the fixture cannot migrate.</summary>
public class ModelTests
{
    [Fact]
    public void TheModelHasNoChangesWithoutAMigration()
    {
        using var context = new IdentityDbContext(DesignTime.OptionsFor<IdentityDbContext>());

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The Identity model has changes without a migration. Run: make ef-add MODULE=Identity NAME=<WhatChanged>, then commit the files under src/Iron.Identity/Data/Migrations.");
    }
}

[Collection(IdentityCollection.Name)]
public class SeedAndInvitationTests(IdentityFixture fixture)
{
    [Fact]
    public async Task TheMigratedSchemaPassesTheSchemaCheck()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider());

        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["schema-check"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSeedCreatesTheFirstAdministratorInvitedWithTheAdministratorRoleAndRunsAgainWithoutDuplicates()
    {
        const string loginId = "seed-check@acme.test";
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), loginId);
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var administrator = await context.Principals.AsNoTracking().SingleAsync(p => p.LoginId == loginId, TestContext.Current.CancellationToken);

        Assert.Equal(PrincipalStatus.Invited, administrator.Status);
        Assert.Equal(PrincipalType.User, administrator.Type);
        Assert.Equal(IdentityFixture.AdministratorDisplayName, administrator.DisplayName);
        Assert.Null(administrator.PasswordHash);
        Assert.NotNull(administrator.InvitationTokenHash);
        var roles = await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().RoleNamesOfAsync(administrator.Id, TestContext.Current.CancellationToken);
        Assert.Equal([SystemRoles.TenantAdministrator], roles);
    }

    [Fact]
    public async Task AcceptingTheInvitationActivatesTheAccountWithAnArgon2idHashAndSpendsTheToken()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var token = await IdentityFixture.ReinviteAdministratorAsync(scope.ServiceProvider);
        var invitations = scope.ServiceProvider.GetRequiredService<InvitationService>();

        var tooShort = await invitations.AcceptAsync(token, "short", null, TestContext.Current.CancellationToken);
        Assert.Equal(IdentityErrors.PasswordTooShort, tooShort.Error.Code);

        var accepted = await invitations.AcceptAsync(token, "a long enough password", "Acme Admin", TestContext.Current.CancellationToken);
        Assert.True(accepted.IsSuccess);
        Assert.Equal(PrincipalStatus.Active, accepted.Value.Status);
        Assert.True(Argon2PasswordHasher.IsArgon2id(accepted.Value.PasswordHash));
        Assert.Null(accepted.Value.InvitationTokenHash);

        var again = await invitations.AcceptAsync(token, "a long enough password", null, TestContext.Current.CancellationToken);
        Assert.Equal(IdentityErrors.InvitationNotFound, again.Error.Code);
    }

    [Fact]
    public async Task AnExpiredInvitationIsRefusedAndNothingChanges()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var token = await IdentityFixture.ReinviteAdministratorAsync(scope.ServiceProvider);
        time.Advance(TimeSpan.FromHours(73));

        var result = await scope.ServiceProvider.GetRequiredService<InvitationService>().AcceptAsync(token, "a long enough password", null, TestContext.Current.CancellationToken);

        Assert.Equal(IdentityErrors.InvitationExpired, result.Error.Code);
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var administrator = await context.Principals.AsNoTracking().SingleAsync(p => p.LoginId == IdentityFixture.AdministratorLoginId, TestContext.Current.CancellationToken);
        Assert.Equal(PrincipalStatus.Invited, administrator.Status);
    }
}
