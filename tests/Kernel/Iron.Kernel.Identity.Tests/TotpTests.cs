using System.Security.Cryptography;
using System.Text;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;

namespace Iron.Kernel.Identity.Tests;

/// <summary>The pieces of a second factor that need no database: the codes, the sealing of secrets, and the cooling arithmetic.</summary>
public class TotpTests
{
    /// <summary>RFC 6238 appendix B's SHA-1 secret: the ASCII string "12345678901234567890".</summary>
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [Proves("ADR-0047")]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void TheCodesAreRfc6238sTestVectorsCutToSixDigits(long unixSeconds, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, Totp.Code(RfcSecret, step));
    }

    [Fact]
    [Proves("ADR-0047")]
    public void ACodeIsAcceptedOneStepEitherSideAndOnlyForAStepLaterThanTheLastOneUsed()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_015);
        var step = Totp.StepAt(now);
        string CodeAt(long s) => Totp.Code(RfcSecret, s);

        Assert.Equal(step - 1, Totp.Match(RfcSecret, CodeAt(step - 1), now, null));
        Assert.Equal(step + 1, Totp.Match(RfcSecret, CodeAt(step + 1), now, null));
        Assert.Equal(step, Totp.Match(RfcSecret, $"{CodeAt(step)[..3]} {CodeAt(step)[3..]}", now, null));
        Assert.Null(Totp.Match(RfcSecret, CodeAt(step - 2), now, null));
        Assert.Null(Totp.Match(RfcSecret, CodeAt(step + 2), now, null));
        Assert.Null(Totp.Match(RfcSecret, CodeAt(step), now, step)); // the same code again: replayed
        Assert.Null(Totp.Match(RfcSecret, CodeAt(step - 1), now, step)); // an older one after a newer one
        Assert.Null(Totp.Match(RfcSecret, "12345", now, null));
    }

    [Fact]
    [Proves("ADR-0047")]
    public void TheSecretTravelsAsBase32AndInTheKeyUriAnAppReads()
    {
        var secret = Totp.NewSecret();
        var uri = Totp.Uri("IronSaaS (acme.localhost)", "anna@acme.test", secret);

        Assert.Equal(20, secret.Length);
        Assert.Equal(secret, Totp.FromBase32(Totp.Base32(secret)));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(RfcSecret));
        Assert.StartsWith("otpauth://totp/IronSaaS%20%28acme.localhost%29:anna%40acme.test?secret=", uri, StringComparison.Ordinal);
        Assert.EndsWith("&algorithm=SHA1&digits=6&period=30", uri, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("TF-074 AC1", "ADR-0047")]
    public void ASealedSecretOpensOnlyWithTheKeyAndForThePersonItWasSealedFor()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var protector = new FactorProtector(new IdentityOptions { FactorKey = key });
        var other = new FactorProtector(new IdentityOptions { FactorKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
        var person = Guid.NewGuid();
        var secret = Totp.NewSecret();

        var sealedSecret = protector.Seal(secret, person);

        Assert.Equal(1 + 12 + 16 + secret.Length, sealedSecret.Length);
        Assert.Equal(-1, sealedSecret.AsSpan().IndexOf(secret));
        Assert.Equal(secret, protector.Open(sealedSecret, person));
        Assert.NotEqual(sealedSecret, protector.Seal(secret, person)); // a new nonce every time
        Assert.ThrowsAny<CryptographicException>(() => protector.Open(sealedSecret, Guid.NewGuid()));
        Assert.ThrowsAny<CryptographicException>(() => other.Open(sealedSecret, person));
        Assert.Throws<InvalidOperationException>(() => new FactorProtector(new IdentityOptions()).Seal(secret, person));
        Assert.Throws<InvalidOperationException>(() => new FactorProtector(new IdentityOptions { FactorKey = Convert.ToBase64String(new byte[16]) }));
    }

    [Fact]
    [Proves("TF-013 AC2", "ADR-0047")]
    public void EveryThirdWrongCodeCoolsSixtySecondsDoublingAndTheTenthLocks()
    {
        var now = DateTimeOffset.Parse("2026-10-22T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var person = new Principal("cool@acme.test", PrincipalType.User, "Cool", null);
        var cooling = new List<TimeSpan?>();
        var locked = new List<bool>();
        for (var failure = 1; failure <= 10; failure++)
        {
            locked.Add(person.RecordSecondFactorFailure(now));
            cooling.Add(person.IsCooling(now) ? person.SecondFactorCoolingUntil - now : null);
            now = person.SecondFactorCoolingUntil is { } until && until > now ? until : now;
        }

        Assert.Equal(
            [null, null, TimeSpan.FromSeconds(60), null, null, TimeSpan.FromSeconds(120), null, null, TimeSpan.FromSeconds(240), null],
            cooling);
        Assert.Equal([false, false, false, false, false, false, false, false, false, true], locked);
        Assert.Equal(0, person.SecondFactorFailures);

        person.RecordSecondFactorFailure(now);
        person.RecordSecondFactorSuccess();
        Assert.Equal((0, null), (person.SecondFactorFailures, person.SecondFactorCoolingUntil));
    }
}
