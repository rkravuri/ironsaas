using System.Security.Cryptography;
using Iron.Kernel.Identity.Services;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// The ID token's checks (ADR-0048), on tokens this test signs: who issued it, for whom, with what key and nonce, and
/// when. No provider is needed to prove them, so every way a token can be wrong is made here.
/// </summary>
public class IdTokenValidatorTests
{
    private const string Issuer = "https://login.example.test/tenant";
    private const string ClientId = "iron-client";
    private const string Nonce = "nonce-of-this-sign-in";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly RsaSecurityKey _key = Key("key-1");
    private readonly RsaSecurityKey _rotated = Key("key-2");

    private static IdTokenExpectations Expected => new(Issuer, ClientId, Nonce, "email", "name", "groups");

    [Fact]
    [Proves("ADR-0048")]
    public async Task ARightTokenGivesItsPersonWithTheProvidersClaimNames()
    {
        var token = Sign(_key, claims: new Dictionary<string, object>
        {
            ["mail"] = "ada@example.test",
            ["email_verified"] = "true",
            ["display"] = "Ada Lovelace",
            ["roles"] = new[] { "buyers", "approvers" },
            ["amr"] = new[] { "pwd", "mfa" },
        });

        var check = await new IdTokenValidator(_time).CheckAsync(token, Expected with { EmailClaim = "mail", NameClaim = "display", GroupsClaim = "roles" }, Keys(_key), TestContext.Current.CancellationToken);

        Assert.Null(check.Failure);
        Assert.Equal(("subject-1", "ada@example.test", true, "Ada Lovelace"), (check.Identity!.Subject, check.Identity.Email, check.Identity.EmailVerified, check.Identity.Name));
        Assert.Equal(["buyers", "approvers"], check.Identity.Groups);
        Assert.True(check.Identity.MultiFactor);
    }

    [Fact]
    [Proves("ADR-0048")]
    public async Task ATokenForAnotherClientOrFromAnotherIssuerOrWithAnotherNonceIsRefused()
    {
        var validator = new IdTokenValidator(_time);
        var ct = TestContext.Current.CancellationToken;

        var audience = await validator.CheckAsync(Sign(_key, audience: "someone-else"), Expected, Keys(_key), ct);
        var issuer = await validator.CheckAsync(Sign(_key, issuer: "https://login.example.test/other"), Expected, Keys(_key), ct);
        var nonce = await validator.CheckAsync(Sign(_key, nonce: "another-sign-in"), Expected, Keys(_key), ct);
        var noNonce = await validator.CheckAsync(Sign(_key, nonce: null), Expected, Keys(_key), ct);
        var party = await validator.CheckAsync(Sign(_key, claims: new Dictionary<string, object> { ["aud"] = new[] { ClientId, "another-api" } }, audience: null), Expected, Keys(_key), ct);

        Assert.Equal("wrong_audience", audience.Failure);
        Assert.Equal("wrong_issuer", issuer.Failure);
        Assert.Equal("wrong_nonce", nonce.Failure);
        Assert.Equal("wrong_nonce", noNonce.Failure);
        Assert.Equal("wrong_authorized_party", party.Failure); // several audiences and no azp naming this client
    }

    [Fact]
    [Proves("ADR-0048")]
    public async Task ATokenPastItsExpiryOrIssuedInTheFutureOrLongAgoIsRefusedBeyondTwoMinutesTolerance()
    {
        var validator = new IdTokenValidator(_time);
        var ct = TestContext.Current.CancellationToken;

        var expired = await validator.CheckAsync(Sign(_key, issuedAt: Now.AddMinutes(-9), expires: Now.AddMinutes(-3)), Expected, Keys(_key), ct);
        var withinTolerance = await validator.CheckAsync(Sign(_key, issuedAt: Now.AddMinutes(-9), expires: Now.AddMinutes(-1)), Expected, Keys(_key), ct);
        var future = await validator.CheckAsync(Sign(_key, issuedAt: Now.AddMinutes(5), expires: Now.AddMinutes(60)), Expected, Keys(_key), ct);
        var old = await validator.CheckAsync(Sign(_key, issuedAt: Now.AddMinutes(-30), expires: Now.AddMinutes(60)), Expected, Keys(_key), ct);

        Assert.Equal("expired", expired.Failure);
        Assert.Null(withinTolerance.Failure);
        Assert.Equal("issue_time", future.Failure);
        Assert.Equal("issue_time", old.Failure);
    }

    [Fact]
    [Proves("ADR-0048")]
    public async Task AKeyTheProviderDoesNotPublishIsRefusedAndANewKeyIsFetchedOnceWhenATokenNamesIt()
    {
        var validator = new IdTokenValidator(_time);
        var ct = TestContext.Current.CancellationToken;
        var reads = new List<bool>();

        // The provider rotated: the set the unit holds lacks key-2, the provider's set now has it.
        var rotated = await validator.CheckAsync(Sign(_rotated), Expected, Keys(reads, stale: [_key], fresh: [_key, _rotated]), ct);
        var readsForRotation = reads.ToList();
        reads.Clear();
        var stranger = await validator.CheckAsync(Sign(Key("key-3")), Expected, Keys(reads, stale: [_key], fresh: [_key, _rotated]), ct);
        var forged = await validator.CheckAsync(Sign(Key("key-1")), Expected, Keys(_key), ct); // another key under a known name

        Assert.Null(rotated.Failure);
        Assert.Equal([false, true], readsForRotation);
        Assert.Equal("unknown_signing_key", stranger.Failure);
        Assert.Equal([false, true], reads); // read again once, then refused
        Assert.Equal("bad_signature", forged.Failure);
    }

    [Fact]
    [Proves("ADR-0048")]
    public async Task ATokenSignedWithASharedSecretOrNotATokenAtAllIsRefused()
    {
        var validator = new IdTokenValidator(_time);
        var ct = TestContext.Current.CancellationToken;
        var shared = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "key-1" };
        var hmac = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(Descriptor(new SigningCredentials(shared, SecurityAlgorithms.HmacSha256), Issuer, ClientId, Nonce, Now, Now.AddMinutes(30), null));

        var symmetric = await validator.CheckAsync(hmac, Expected, Keys(_key), ct);
        var garbage = await validator.CheckAsync("not.a.token", Expected, Keys(_key), ct);

        Assert.NotNull(symmetric.Failure);
        Assert.Equal("not_a_token", garbage.Failure);
    }

    private static RsaSecurityKey Key(string id) => new(RSA.Create(2048)) { KeyId = id };

    private static Func<bool, CancellationToken, Task<Result<JsonWebKeySet>>> Keys(params RsaSecurityKey[] keys) => (_, _) => Task.FromResult<Result<JsonWebKeySet>>(Set(keys));

    private static Func<bool, CancellationToken, Task<Result<JsonWebKeySet>>> Keys(List<bool> reads, RsaSecurityKey[] stale, RsaSecurityKey[] fresh) => (refresh, _) =>
    {
        reads.Add(refresh);
        return Task.FromResult<Result<JsonWebKeySet>>(Set(refresh ? fresh : stale));
    };

    private static JsonWebKeySet Set(IEnumerable<RsaSecurityKey> keys)
    {
        var set = new JsonWebKeySet();
        foreach (var key in keys)
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(key.Rsa.ExportParameters(false)) { KeyId = key.KeyId });
            jwk.Use = "sig";
            set.Keys.Add(jwk);
        }

        return set;
    }

    private string Sign(RsaSecurityKey key, string? issuer = Issuer, string? audience = ClientId, string? nonce = Nonce, DateTimeOffset? issuedAt = null, DateTimeOffset? expires = null, IDictionary<string, object>? claims = null) =>
        new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(Descriptor(new SigningCredentials(key, SecurityAlgorithms.RsaSha256), issuer, audience, nonce, issuedAt ?? Now.AddSeconds(-5), expires ?? Now.AddMinutes(55), claims));

    private static SecurityTokenDescriptor Descriptor(SigningCredentials credentials, string? issuer, string? audience, string? nonce, DateTimeOffset issuedAt, DateTimeOffset expires, IDictionary<string, object>? claims)
    {
        var payload = new Dictionary<string, object>(claims ?? new Dictionary<string, object>(), StringComparer.Ordinal)
        {
            ["sub"] = "subject-1",
        };
        if (nonce is not null)
        {
            payload["nonce"] = nonce;
        }

        return new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = issuedAt.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = payload,
            SigningCredentials = credentials,
        };
    }
}
