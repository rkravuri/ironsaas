using Iron.Audit.Data;
using Iron.Identity.Contracts;
using Iron.Identity.Data;
using Iron.Identity.Services;
using Iron.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Identity.Tests;

[Collection(IdentityCollection.Name)]
public class LoginAndSessionTests(IdentityFixture fixture)
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task AWrongPasswordGetsTheFixedMessageCountsTheAttemptAndIsAudited()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var loginId = $"wrong-{Ids.New():N}@acme.test";
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, loginId, Password);

        var result = await scope.ServiceProvider.GetRequiredService<LoginService>().LoginAsync(loginId, "not the password", TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrors.LoginInvalid, result.Error.Code);
        Assert.Equal(LoginService.FailureMessage, result.Error.Message);
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await context.Entry(user).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, user.FailedLoginAttempts);
        Assert.Equal(time.GetUtcNow(), user.LastFailedLoginAt);
        var audit = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .SingleAsync(e => e.ActorId == user.Id && e.EventType == IdentityAuditEvents.LoginFailed, TestContext.Current.CancellationToken);
        Assert.Equal("wrong_password", audit.Reason);
    }

    [Fact]
    public async Task AnUnknownLoginAndAnInvitedAccountGetTheSameMessage()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        var login = scope.ServiceProvider.GetRequiredService<LoginService>();
        var invitedUser = await IdentityFixture.CreateInvitedUserAsync(scope.ServiceProvider, $"invited-{Ids.New():N}@acme.test");

        var unknown = await login.LoginAsync($"nobody-{Ids.New():N}@acme.test", Password, TestContext.Current.CancellationToken);
        var invited = await login.LoginAsync(invitedUser.LoginId, Password, TestContext.Current.CancellationToken);

        Assert.Equal(LoginService.FailureMessage, unknown.Error.Message);
        Assert.Equal(LoginService.FailureMessage, invited.Error.Message);
        Assert.Equal(unknown.Error.Code, invited.Error.Code);
    }

    [Fact]
    public async Task ACorrectLoginCreatesASessionRowAndAnAuditEventInOneTransaction()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var loginId = $"right-{Ids.New():N}@acme.test";
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, loginId, Password);

        var result = await scope.ServiceProvider.GetRequiredService<LoginService>().LoginAsync(loginId.ToUpperInvariant(), Password, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var outcome = result.Value;
        Assert.Equal(user.Id, outcome.Principal.Id);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromHours(12), outcome.Session.ExpiresAt);
        Assert.Equal(SecureTokens.Hash(outcome.Token), outcome.Session.TokenHash);
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.True(await context.Sessions.AsNoTracking().AnyAsync(s => s.Id == outcome.Session.Id && s.EndedAt == null, TestContext.Current.CancellationToken));
        var audit = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .SingleAsync(e => e.ActorId == user.Id && e.EventType == IdentityAuditEvents.LoginSucceeded, TestContext.Current.CancellationToken);
        Assert.Equal(outcome.Session.Id, audit.RecordId);
    }

    [Fact]
    public async Task ALoginThatRacesAnotherAttemptOnTheSameAccountStillSucceeds()
    {
        // While this login verifies the password, another attempt (a wrong one) saves its stamp on the same row.
        // Seen by Playwright running two browsers against one administrator: the second save hit the version column.
        var time = new FakeTimeProvider();
        var loginId = $"raced-{Ids.New():N}@acme.test";
        var interfering = fixture.BuildServices(time);
        await using var provider = fixture.BuildServices(time, configure: services => services.AddSingleton<IPasswordHasher>(sp => new HookedHasher(new Argon2PasswordHasher(sp.GetRequiredService<IdentityOptions>()), () =>
        {
            using var other = interfering.CreateScope();
            var context = other.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var row = context.Principals.Single(p => p.LoginId == loginId);
            row.RecordFailedLogin(time.GetUtcNow());
            context.SaveChanges();
        })));
        await using var scope = provider.CreateAsyncScope();
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, loginId, Password);

        var result = await scope.ServiceProvider.GetRequiredService<LoginService>().LoginAsync(loginId, Password, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        await using var check = provider.CreateAsyncScope();
        var stored = await check.ServiceProvider.GetRequiredService<IdentityDbContext>().Principals.AsNoTracking().SingleAsync(p => p.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.Equal(0, stored.FailedLoginAttempts); // the success wrote last and reset the count
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<IdentityDbContext>().Sessions.AsNoTracking().CountAsync(s => s.PrincipalId == user.Id, TestContext.Current.CancellationToken));
        await interfering.DisposeAsync();
    }

    /// <summary>Verifies like the real hasher, and runs a hook first so a test can make something happen mid-login.</summary>
    private sealed class HookedHasher(IPasswordHasher inner, Action onVerify) : IPasswordHasher
    {
        public string Hash(string password) => inner.Hash(password);

        public bool Verify(string password, string encodedHash)
        {
            onVerify();
            return inner.Verify(password, encodedHash);
        }

        public bool VerifyOrDecoy(string password, string? encodedHash) => inner.VerifyOrDecoy(password, encodedHash);
    }

    [Fact]
    public async Task ASessionEndsAfterSixtyIdleMinutesAndTheExpiryIsAudited()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var loginId = $"idle-{Ids.New():N}@acme.test";
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, loginId, Password);
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
        var (session, token) = await sessions.StartAsync(user, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(59));
        var alive = await sessions.ResolveAsync(token, TestContext.Current.CancellationToken);
        Assert.True(alive.IsAlive);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(60), alive.Principal!.IdleExpiresAt);

        time.Advance(TimeSpan.FromMinutes(61));
        var expired = await sessions.ResolveAsync(token, TestContext.Current.CancellationToken);
        Assert.True(expired.IsExpired);
        Assert.Equal(SessionEndReasons.Idle, expired.ExpiryReason);
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var row = await context.Sessions.AsNoTracking().SingleAsync(s => s.Id == session.Id, TestContext.Current.CancellationToken);
        Assert.Equal(SessionEndReasons.Idle, row.EndReason);
        Assert.NotNull(row.EndedAt);
        var audit = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .SingleAsync(e => e.ActorId == user.Id && e.EventType == IdentityAuditEvents.SessionExpired, TestContext.Current.CancellationToken);
        Assert.Equal(SessionEndReasons.Idle, audit.Reason);
    }

    [Fact]
    public async Task ASessionEndsAtTwelveHoursHoweverActiveItWas()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var user = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"busy-{Ids.New():N}@acme.test", Password);
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
        var (_, token) = await sessions.StartAsync(user, TestContext.Current.CancellationToken);

        for (var hour = 0; hour < 12; hour++)
        {
            time.Advance(TimeSpan.FromMinutes(59));
            Assert.True((await sessions.ResolveAsync(token, TestContext.Current.CancellationToken)).IsAlive, $"hour {hour}");
        }

        time.Advance(TimeSpan.FromMinutes(20));
        var expired = await sessions.ResolveAsync(token, TestContext.Current.CancellationToken);

        Assert.True(expired.IsExpired);
        Assert.Equal(SessionEndReasons.Absolute, expired.ExpiryReason);
    }

    [Fact]
    public async Task AnUnknownTokenIsNeitherAliveNorExpired()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();

        var resolution = await scope.ServiceProvider.GetRequiredService<SessionService>().ResolveAsync(SecureTokens.New(), TestContext.Current.CancellationToken);

        Assert.False(resolution.IsAlive);
        Assert.False(resolution.IsExpired);
    }
}
