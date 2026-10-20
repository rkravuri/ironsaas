using Iron.Kernel.Identity.Services;

namespace Iron.Kernel.Identity.Tests;

/// <summary>TF-011 AC3: Argon2id, unique salt, self-describing parameters.</summary>
public class PasswordHasherTests
{
    private static readonly Argon2PasswordHasher Hasher = new(new IdentityOptions
    {
        Argon2 = new IdentityOptions.Argon2Options { MemoryKb = 8192, Iterations = 1, Parallelism = 1 },
    });

    [Fact]
    [Proves("TF-011 AC3", "ADR-0014")]
    public void ProducesAnArgon2idPhcStringWithTheParameters()
    {
        var hash = Hasher.Hash("correct horse battery staple");

        Assert.StartsWith("$argon2id$v=19$m=8192,t=1,p=1$", hash, StringComparison.Ordinal);
        Assert.True(Argon2PasswordHasher.IsArgon2id(hash));
        Assert.Equal(6, hash.Split('$').Length);
    }

    [Fact]
    [Proves("ADR-0014")]
    public void VerifiesTheRightPasswordAndRefusesOthers()
    {
        var hash = Hasher.Hash("correct horse battery staple");

        Assert.True(Hasher.Verify("correct horse battery staple", hash));
        Assert.False(Hasher.Verify("correct horse battery stapler", hash));
        Assert.False(Hasher.Verify(string.Empty, hash));
    }

    [Fact]
    [Proves("TF-011 AC3", "ADR-0014")]
    public void SaltsEveryHashDifferently()
    {
        Assert.NotEqual(Hasher.Hash("same password"), Hasher.Hash("same password"));
    }

    [Theory]
    [Untraced("How the verifier treats a malformed or non-Argon2id stored value is stated neither by TF-011 nor by ADR-0014.")]
    [InlineData("")]
    [InlineData("plain-text")]
    [InlineData("$argon2i$v=19$m=8192,t=1,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaA")]
    [InlineData("$argon2id$v=19$m=0,t=1,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaA")]
    [InlineData("$argon2id$v=19$m=8192,t=1,p=1$not base64!$aGFzaA")]
    public void RefusesAStoredValueThatIsNotAValidArgon2idHash(string stored)
    {
        Assert.False(Hasher.Verify("anything", stored));
    }

    [Fact]
    [Proves("ADR-0014")]
    public void ReadsTheParametersFromTheHashNotFromTheOptions()
    {
        var hash = Hasher.Hash("a password of some length");
        var stronger = new Argon2PasswordHasher(new IdentityOptions { Argon2 = new IdentityOptions.Argon2Options { MemoryKb = 16384, Iterations = 2, Parallelism = 2 } });

        Assert.True(stronger.Verify("a password of some length", hash));
    }
}
