using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Security.Keys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Identity.Tests;

/// <summary>TF-020: issuer, audience, lifetime with zero skew, signature, and the tenant claim (TF-003).</summary>
[Collection(IdentityCollection.Name)]
public class TokenTests(IdentityFixture fixture)
{
    [Fact]
    [Proves("ADR-0015")]
    public async Task AnIssuedTokenValidatesWithItsClaims()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
        var client = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"client-{Ids.New():N}", "a secret of some length");

        var issued = await tokens.IssueAsync(client, "read", TestContext.Current.CancellationToken);
        var validation = await tokens.ValidateAsync(issued.AccessToken);

        Assert.True(validation.IsValid, validation.Failure);
        Assert.Equal(client.Id, validation.Claims!.PrincipalId);
        Assert.Equal(issued.TokenId, validation.Claims.TokenId);
        Assert.Equal(client.LoginId, validation.Claims.ClientId);
        Assert.Equal("read", validation.Claims.Scope);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(15), validation.Claims.ExpiresAt);
        Assert.Equal(3, issued.AccessToken.Split('.').Length);
    }

    [Fact]
    [Proves("TF-020 AC2", "ADR-0015")]
    public async Task AnExpiredTokenIsRefusedWithZeroSkew()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
        var client = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"client-{Ids.New():N}", "a secret of some length");
        var issued = await tokens.IssueAsync(client, null, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.True((await tokens.ValidateAsync(issued.AccessToken)).IsValid);

        time.Advance(TimeSpan.FromSeconds(1));
        var expired = await tokens.ValidateAsync(issued.AccessToken);
        Assert.False(expired.IsValid);
        Assert.Equal(TokenService.ExpiredReason, expired.Failure);
    }

    [Fact]
    [Proves("TF-003 AC1", "ADR-0015")]
    public async Task ATokenOfAnotherTenantIsRefusedEvenWithTheSameKey()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var client = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"client-{Ids.New():N}", "a secret of some length");
        var keys = scope.ServiceProvider.GetRequiredService<SigningKeys>();
        var options = scope.ServiceProvider.GetRequiredService<TokenOptions>();
        var bravo = new TokenService(keys, options, new TenantContext(TenantId.Parse("bravo"), ["bravo.localhost"]), time);
        var acme = scope.ServiceProvider.GetRequiredService<TokenService>();

        var fromBravo = await bravo.IssueAsync(client, null, TestContext.Current.CancellationToken);
        var validation = await acme.ValidateAsync(fromBravo.AccessToken);

        Assert.False(validation.IsValid);
    }

    [Fact]
    [Proves("ADR-0015")]
    public async Task ATokenSignedWithAnotherKeyOrTamperedIsRefused()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var client = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"client-{Ids.New():N}", "a secret of some length");
        var acme = scope.ServiceProvider.GetRequiredService<TokenService>();
        using var otherService = new LocalKeyService(new KeyOptions { Local = new KeyOptions.LocalKeyOptions { Material = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) } });
        var otherKeys = new SigningKeys(otherService, time);
        var stranger = new TokenService(otherKeys, scope.ServiceProvider.GetRequiredService<TokenOptions>(), IdentityFixture.Tenant, time);

        var foreign = await stranger.IssueAsync(client, null, TestContext.Current.CancellationToken);
        Assert.False((await acme.ValidateAsync(foreign.AccessToken)).IsValid);

        var genuine = (await acme.IssueAsync(client, null, TestContext.Current.CancellationToken)).AccessToken;
        var parts = genuine.Split('.');
        var tampered = parts[0] + "." + parts[1] + "." + new string('A', parts[2].Length);
        Assert.False((await acme.ValidateAsync(tampered)).IsValid);
        Assert.False((await acme.ValidateAsync("not a token")).IsValid);
    }

    [Fact]
    [Proves("TF-075 AC2", "ADR-0054")]
    public async Task ATokenSignedBeforeARotationStaysValidUntilItExpiresAndTheNewVersionSignsNewOnes()
    {
        var time = new FakeTimeProvider();
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
        var keys = scope.ServiceProvider.GetRequiredService<SigningKeys>();
        var administration = scope.ServiceProvider.GetRequiredService<IKeyAdministration>();
        var client = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"client-{Ids.New():N}", "a secret of some length");
        var before = await tokens.IssueAsync(client, null, ct);

        time.Advance(TimeSpan.FromMinutes(1));
        await administration.RotateAsync(KeyPurposes.KeyName("local", KeyPurpose.Signing), ct);
        time.Advance(SigningKeys.RefreshInterval);
        var after = await tokens.IssueAsync(client, null, ct);
        var published = await keys.JsonWebKeySetAsync(ct);

        Assert.NotEqual(KeyIdOf(before.AccessToken), KeyIdOf(after.AccessToken));
        Assert.Equal(2, published.Keys.Count);
        Assert.True((await tokens.ValidateAsync(before.AccessToken)).IsValid);
        Assert.True((await tokens.ValidateAsync(after.AccessToken)).IsValid);

        time.Advance(TimeSpan.FromMinutes(13));
        var expired = await tokens.ValidateAsync(before.AccessToken);
        Assert.Equal(TokenService.ExpiredReason, expired.Failure);

        await administration.RetireAsync(KeyPurposes.KeyName("local", KeyPurpose.Signing), 2, ct);
        time.Advance(SigningKeys.RefreshInterval);
        Assert.Single((await keys.JsonWebKeySetAsync(ct)).Keys);
        Assert.True((await tokens.ValidateAsync(after.AccessToken)).IsValid);
    }

    private static string KeyIdOf(string token) => new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).Kid;
}
