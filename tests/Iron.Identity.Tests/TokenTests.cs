using Iron.Identity.Data;
using Iron.Identity.Services;
using Iron.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Identity.Tests;

/// <summary>TF-020: issuer, audience, lifetime with zero skew, signature, and the tenant claim (TF-003).</summary>
[Collection(IdentityCollection.Name)]
public class TokenTests(IdentityFixture fixture)
{
    [Fact]
    public async Task AnIssuedTokenValidatesWithItsClaims()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
        var client = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"client-{Ids.New():N}", "a secret of some length");

        var issued = tokens.Issue(client, "read");
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
    public async Task AnExpiredTokenIsRefusedWithZeroSkew()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
        var client = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"client-{Ids.New():N}", "a secret of some length");
        var issued = tokens.Issue(client, null);

        time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.True((await tokens.ValidateAsync(issued.AccessToken)).IsValid);

        time.Advance(TimeSpan.FromSeconds(1));
        var expired = await tokens.ValidateAsync(issued.AccessToken);
        Assert.False(expired.IsValid);
        Assert.Equal(TokenService.ExpiredReason, expired.Failure);
    }

    [Fact]
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

        var fromBravo = bravo.Issue(client, null);
        var validation = await acme.ValidateAsync(fromBravo.AccessToken);

        Assert.False(validation.IsValid);
    }

    [Fact]
    public async Task ATokenSignedWithAnotherKeyOrTamperedIsRefused()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time);
        await using var scope = provider.CreateAsyncScope();
        var client = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"client-{Ids.New():N}", "a secret of some length");
        var acme = scope.ServiceProvider.GetRequiredService<TokenService>();
        using var otherKeys = new SigningKeys(new TokenOptions(), NullLogger<SigningKeys>.Instance);
        var stranger = new TokenService(otherKeys, scope.ServiceProvider.GetRequiredService<TokenOptions>(), IdentityFixture.Tenant, time);

        var foreign = stranger.Issue(client, null);
        Assert.False((await acme.ValidateAsync(foreign.AccessToken)).IsValid);

        var genuine = acme.Issue(client, null).AccessToken;
        var parts = genuine.Split('.');
        var tampered = parts[0] + "." + parts[1] + "." + new string('A', parts[2].Length);
        Assert.False((await acme.ValidateAsync(tampered)).IsValid);
        Assert.False((await acme.ValidateAsync("not a token")).IsValid);
    }
}
