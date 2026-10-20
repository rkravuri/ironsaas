using System.Text.Json;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Settings.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// Identity's settings (TF-021, TF-018, ADR-0036): a new session takes the tenant's idle and absolute limits and keeps
/// them, so <c>/me</c> answers them and the shell's warning follows; without a tenant value the configured system default
/// applies; and an invitation takes the validity of the unit it is sent into, walking up the tree.
/// </summary>
[Collection(IdentityCollection.Name)]
public class IdentitySettingsTests(IdentityFixture fixture)
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task ANewSessionTakesTheTenantsIdleLimitAndAnOpenSessionKeepsItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var loginId = $"limits-{Ids.New():N}@acme.test";
        await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, loginId, Password);
        var login = scope.ServiceProvider.GetRequiredService<LoginService>();
        var writer = scope.ServiceProvider.GetRequiredService<SettingsWriter>();

        var before = (await login.LoginAsync(loginId, Password, ct)).Value.Session;
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(60), before.IdleExpiresAt(TimeSpan.FromMinutes(60)));

        try
        {
            Assert.True((await writer.SetAsync(IdentitySettings.SessionIdleMinutes.Key, SettingPlace.Tenant, JsonSerializer.SerializeToElement(30), ct)).IsSuccess);
            Assert.True((await writer.SetAsync(IdentitySettings.SessionAbsoluteHours.Key, SettingPlace.Tenant, JsonSerializer.SerializeToElement(8), ct)).IsSuccess);

            var after = (await login.LoginAsync(loginId, Password, ct)).Value.Session;
            Assert.Equal(30, after.IdleMinutes);
            Assert.Equal(time.GetUtcNow() + TimeSpan.FromHours(8), after.ExpiresAt);
            var described = scope.ServiceProvider.GetRequiredService<SessionService>().Describe((await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Principals.FindAsync([after.PrincipalId], ct))!, after);
            Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(30), described.IdleExpiresAt);

            // The session opened before the change keeps the hour it started with; the new one ends after 30 minutes.
            time.Advance(TimeSpan.FromMinutes(31));
            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            Assert.Null(before.ExpiryReason(time.GetUtcNow(), TimeSpan.FromMinutes(60)));
            Assert.Equal(SessionEndReasons.Idle, after.ExpiryReason(time.GetUtcNow(), TimeSpan.FromMinutes(60)));
            Assert.Equal((TimeSpan.FromMinutes(30), TimeSpan.FromHours(8)), await sessions.LimitsAsync(ct));
        }
        finally
        {
            _ = await writer.ResetAsync(IdentitySettings.SessionIdleMinutes.Key, SettingPlace.Tenant, ct);
            _ = await writer.ResetAsync(IdentitySettings.SessionAbsoluteHours.Key, SettingPlace.Tenant, ct);
        }
    }

    [Fact]
    public async Task WithoutATenantValueTheConfiguredSystemDefaultApplies()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), settings: new Dictionary<string, string?> { ["Identity:Session:IdleMinutes"] = "45", ["Identity:Session:AbsoluteHours"] = "6" });
        await using var scope = provider.CreateAsyncScope();

        Assert.Equal((TimeSpan.FromMinutes(45), TimeSpan.FromHours(6)), await scope.ServiceProvider.GetRequiredService<SessionService>().LimitsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnInvitationTakesTheValidityOfTheUnitItIsSentIntoWalkingUpToTheTenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var tree = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = (await scope.ServiceProvider.GetRequiredService<IOrganisationUnits>().RootAsync(ct))!.Id;
        var region = (await tree.CreateAsync(root, "Invite region", $"invite-region-{Ids.New():N}"[..30], KernelOrganisationKinds.Unit, ct)).Value.Id;
        var branch = (await tree.CreateAsync(region, "Invite branch", $"invite-branch-{Ids.New():N}"[..30], KernelOrganisationKinds.Unit, ct)).Value.Id;
        var writer = scope.ServiceProvider.GetRequiredService<SettingsWriter>();
        var invitations = scope.ServiceProvider.GetRequiredService<InvitationService>();

        var elsewhere = await InvitedAsync(scope.ServiceProvider, root);
        await invitations.InviteAsync(elsewhere, ct);
        // The default: three days, TF-018's 72 hours.
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromHours(72), elsewhere.InvitationExpiresAt);

        try
        {
            Assert.True((await writer.SetAsync(IdentitySettings.InvitationValidDays.Key, SettingPlace.Unit(region), JsonSerializer.SerializeToElement(14), ct)).IsSuccess);
            Assert.True((await writer.SetAsync(IdentitySettings.InvitationValidDays.Key, SettingPlace.Tenant, JsonSerializer.SerializeToElement(5), ct)).IsSuccess);

            // Into the branch, which sits in the region: the region's 14 days, over the tenant's 5.
            var intoBranch = await InvitedAsync(scope.ServiceProvider, branch);
            await invitations.InviteAsync(intoBranch, ct);
            Assert.Equal(time.GetUtcNow() + TimeSpan.FromDays(14), intoBranch.InvitationExpiresAt);

            await invitations.InviteAsync(elsewhere, ct);
            Assert.Equal(time.GetUtcNow() + TimeSpan.FromDays(5), elsewhere.InvitationExpiresAt);
        }
        finally
        {
            _ = await writer.ResetAsync(IdentitySettings.InvitationValidDays.Key, SettingPlace.Unit(region), ct);
            _ = await writer.ResetAsync(IdentitySettings.InvitationValidDays.Key, SettingPlace.Tenant, ct);
        }
    }

    private static async Task<Principal> InvitedAsync(IServiceProvider scope, Guid homeUnit)
    {
        var user = await IdentityFixture.CreateInvitedUserAsync(scope, $"invited-{Ids.New():N}@acme.test");
        user.SetHomeUnit(homeUnit);
        await scope.GetRequiredService<IdentityDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }
}
