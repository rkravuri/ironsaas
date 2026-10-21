using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Identity.Tests;

/// <summary>Runs without the database on purpose, so its message shows even when the fixture cannot migrate.</summary>
public class ModelTests
{
    [Fact]
    [Proves("ADR-0012")]
    public void TheModelHasNoChangesWithoutAMigration()
    {
        using var context = new IdentityDbContext(DesignTime.OptionsFor<IdentityDbContext>());

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The Identity model has changes without a migration. Run: make ef-add MODULE=Identity NAME=<WhatChanged>, then commit the files under src/Kernel/Iron.Kernel.Identity/Data/Migrations.");
    }
}

[Collection(IdentityCollection.Name)]
public class SeedAndInvitationTests(IdentityFixture fixture)
{
    [Fact]
    [Proves("ADR-0008", "ADR-0014")]
    public async Task TheMigratedSchemaPassesTheSchemaCheck()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider());

        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["schema-check"], TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("TF-088 AC1", "ADR-0014")]
    public async Task TheSeedCreatesTheFirstAdministratorInvitedWithTheAdministratorRoleAndASecondRunReissuesTheInvitation()
    {
        const string loginId = "seed-check@acme.test";
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), loginId);
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
        string? firstTokenHash;
        await using (var first = provider.CreateAsyncScope())
        {
            firstTokenHash = (await first.ServiceProvider.GetRequiredService<IdentityDbContext>().Principals.AsNoTracking().SingleAsync(p => p.LoginId == loginId, TestContext.Current.CancellationToken)).InvitationTokenHash;
        }

        // A seed that runs again (a resumed provisioning step) keeps one administrator and issues a fresh invitation.
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var administrator = await context.Principals.AsNoTracking().SingleAsync(p => p.LoginId == loginId, TestContext.Current.CancellationToken);

        Assert.NotNull(firstTokenHash);
        Assert.NotEqual(firstTokenHash, administrator.InvitationTokenHash);
        Assert.Equal(PrincipalStatus.Invited, administrator.Status);
        Assert.Equal(PrincipalType.User, administrator.Type);
        Assert.Equal(IdentityFixture.AdministratorDisplayName, administrator.DisplayName);
        Assert.Null(administrator.PasswordHash);
        Assert.NotNull(administrator.InvitationTokenHash);
        var roles = await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().RoleCodesOfAsync(administrator.Id, TestContext.Current.CancellationToken);
        Assert.Equal([SystemRoles.TenantAdministrator], roles);
    }

    [Fact]
    [Proves("ADR-0030")]
    public async Task TheSeedGivesTheAdministratorAndTheServiceAccountsTheRootUnitOfKindRootAsHomeUnit()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        var root = await scope.ServiceProvider.GetRequiredService<IOrganisationUnits>().RootAsync(TestContext.Current.CancellationToken);
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var seeded = await context.Principals.AsNoTracking()
            .Where(p => p.LoginId == IdentityFixture.AdministratorLoginId || p.LoginId == IdentityFixture.SmokeClientId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(root);
        Assert.Equal(KernelOrganisationKinds.Root, root.Kind);
        Assert.Equal(IdentityFixture.Tenant.Id.Value, root.Code);
        Assert.Equal(2, seeded.Count);
        Assert.All(seeded, principal => Assert.Equal(root.Id, principal.HomeUnitId));
        var administrator = seeded.Single(p => p.LoginId == IdentityFixture.AdministratorLoginId);
        var homeUnitSet = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.EventType == IdentityAuditEvents.HomeUnitSet && e.RecordId == administrator.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("seed", Assert.Single(homeUnitSet).Reason);
    }

    [Fact]
    [Proves("TF-011 AC3", "TF-018 AC1", "ADR-0014")]
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
    [Proves("TF-018 AC2")]
    public async Task AnExpiredInvitationIsRefusedAndNothingChanges()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var token = await IdentityFixture.ReinviteAdministratorAsync(scope.ServiceProvider);
        // An hour past the default validity of identity.invitation.valid_days (ADR-0036).
        time.Advance(TimeSpan.FromDays(IdentitySettings.InvitationValidDays.DefaultValue) + TimeSpan.FromHours(1));

        var result = await scope.ServiceProvider.GetRequiredService<InvitationService>().AcceptAsync(token, "a long enough password", null, TestContext.Current.CancellationToken);

        Assert.Equal(IdentityErrors.InvitationExpired, result.Error.Code);
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var administrator = await context.Principals.AsNoTracking().SingleAsync(p => p.LoginId == IdentityFixture.AdministratorLoginId, TestContext.Current.CancellationToken);
        Assert.Equal(PrincipalStatus.Invited, administrator.Status);
    }

    [Fact]
    [Proves("ADR-0028")]
    public async Task TheSeedGivesAnExistingServiceAccountTheNewSecretAndAuditsTheChange()
    {
        // What a healed unit sends (ADR-0028): the account exists with the secret the store lost, the seed brings a new one.
        const string clientId = "rotation-client";
        const string lost = "the secret the store lost, of some length";
        const string fresh = "the secret the fleet made after the heal";
        var account = await SeedServiceAccountAsync(clientId, lost);
        var changesBefore = await SecretChangesOfAsync(account.Id);

        var after = await SeedServiceAccountAsync(clientId, fresh);

        await using var provider = fixture.BuildServices(new FakeTimeProvider());
        var hasher = provider.GetRequiredService<IPasswordHasher>();
        Assert.Equal(account.Id, after.Id);
        Assert.True(hasher.Verify(fresh, after.PasswordHash!));
        Assert.False(hasher.Verify(lost, after.PasswordHash!));
        Assert.Equal(0, changesBefore);
        Assert.Equal(1, await SecretChangesOfAsync(account.Id));
    }

    [Fact]
    [Proves("TF-088 AC1", "ADR-0028")]
    public async Task TheSeedWritesNothingWhenAnExistingServiceAccountKeepsItsSecret()
    {
        const string clientId = "steady-client";
        const string secret = "a secret that stays the same, of some length";
        var account = await SeedServiceAccountAsync(clientId, secret);

        var again = await SeedServiceAccountAsync(clientId, secret);

        Assert.Equal(account.PasswordHash, again.PasswordHash);
        Assert.Equal(0, await SecretChangesOfAsync(account.Id));
    }

    /// <summary>Runs the seed with one service account in Seed:ServiceAccounts and returns the account as stored.</summary>
    private async Task<Principal> SeedServiceAccountAsync(string clientId, string secret)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Seed:ServiceAccounts:0:ClientId"] = clientId,
            ["Seed:ServiceAccounts:0:Secret"] = secret,
        };
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), settings: settings);
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Principals.AsNoTracking().SingleAsync(p => p.LoginId == clientId, TestContext.Current.CancellationToken);
    }

    private async Task<int> SecretChangesOfAsync(Guid accountId)
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .CountAsync(e => e.ActorId == accountId && e.EventType == IdentityAuditEvents.ServiceAccountSecretChanged && e.RecordId == accountId, TestContext.Current.CancellationToken);
    }
}
