using System.Security.Cryptography;
using System.Text;
using Iron.Kernel.Security.Keys;
using Iron.Kernel.Security.Secrets;

namespace Iron.Kernel.Security.Tests;

/// <summary>
/// The key service (ADR-0054) on OpenBao's transit engine, as on the laptop, and in the process, as for the tests: values
/// carry their version, rotation adds one, re-wrapping moves a value without the application seeing it, a retired version
/// does nothing, signatures verify with the version's public key, and a unit's token uses its own tenant's keys only.
/// </summary>
[Collection(OpenBaoCollection.Name)]
public class KeyServiceTests(OpenBaoFixture openBao)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("ADR-0054")]
    public async Task TheTransitEngineEncryptsRewrapsAndDecryptsWhileTheApplicationHoldsNoKey()
    {
        var prefix = NewPrefix();
        using var keys = new TransitKeyService(openBao.Options(), new KeyOptions { Prefix = prefix });
        await keys.EnsureAsync(KeyPurposes.KeyName(prefix, KeyPurpose.Data), KeyKind.Encryption, Cancel);

        var first = await keys.EncryptAsync(KeyPurpose.Data, Encoding.UTF8.GetBytes("GB33BUKB20201555555555"), Cancel);
        await keys.RotateAsync(KeyPurposes.KeyName(prefix, KeyPurpose.Data), Cancel);
        var moved = (await keys.RewrapAsync(KeyPurpose.Data, [first], Cancel))[0];

        Assert.StartsWith("vault:v1:", first, StringComparison.Ordinal);
        Assert.DoesNotContain("20201555", first, StringComparison.Ordinal);
        Assert.Equal(2, KeyText.VersionOf(moved));
        Assert.Equal("GB33BUKB20201555555555", Encoding.UTF8.GetString(await keys.DecryptAsync(KeyPurpose.Data, moved, Cancel)));
        Assert.Equal(new KeyVersions(2, 1).Live, (await keys.VersionsAsync(KeyPurpose.Data, Cancel)).Live);
    }

    [Fact]
    [Proves("TF-075 AC1", "ADR-0054")]
    public async Task ARetiredVersionNeitherDecryptsNorHashesAndTheNewOneDoes()
    {
        var prefix = NewPrefix();
        using var keys = new TransitKeyService(openBao.Options(), new KeyOptions { Prefix = prefix });
        var data = KeyPurposes.KeyName(prefix, KeyPurpose.Data);
        var index = KeyPurposes.KeyName(prefix, KeyPurpose.Index);
        await keys.EnsureAsync(data, KeyKind.Encryption, Cancel);
        await keys.EnsureAsync(index, KeyKind.Hash, Cancel);
        var old = await keys.EncryptAsync(KeyPurpose.Data, Encoding.UTF8.GetBytes("DE89370400440532013000"), Cancel);
        var oldHash = await keys.HashAsync(KeyPurpose.Index, Encoding.UTF8.GetBytes("DE89370400440532013000"), null, Cancel);

        await keys.RotateAsync(data, Cancel);
        await keys.RotateAsync(index, Cancel);
        var moved = (await keys.RewrapAsync(KeyPurpose.Data, [old], Cancel))[0];
        var retired = await keys.RetireAsync(data, 2, Cancel);
        await keys.RetireAsync(index, 2, Cancel);

        Assert.Equal((2, 2), (retired.Latest, retired.Minimum));
        await Assert.ThrowsAsync<KeyVersionRetiredException>(() => keys.DecryptAsync(KeyPurpose.Data, old, Cancel));
        await Assert.ThrowsAsync<KeyVersionRetiredException>(() => keys.HashAsync(KeyPurpose.Index, Encoding.UTF8.GetBytes("DE89370400440532013000"), 1, Cancel));
        Assert.Equal("DE89370400440532013000", Encoding.UTF8.GetString(await keys.DecryptAsync(KeyPurpose.Data, moved, Cancel)));
        Assert.NotEqual(oldHash, await keys.HashAsync(KeyPurpose.Index, Encoding.UTF8.GetBytes("DE89370400440532013000"), null, Cancel));
    }

    [Fact]
    [Proves("ADR-0054")]
    public async Task ASignatureVerifiesWithThePublicKeyOfTheVersionThatMadeIt()
    {
        var prefix = NewPrefix();
        using var keys = new TransitKeyService(openBao.Options(), new KeyOptions { Prefix = prefix });
        var signing = KeyPurposes.KeyName(prefix, KeyPurpose.Signing);
        await keys.EnsureAsync(signing, KeyKind.Signing, Cancel);
        await keys.RotateAsync(signing, Cancel);
        var data = Encoding.ASCII.GetBytes("eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJ4In0");

        var older = await keys.SignAsync(KeyPurpose.Signing, data, 1, Cancel);
        var latest = await keys.SignAsync(KeyPurpose.Signing, data, null, Cancel);
        var versions = await keys.VersionsAsync(KeyPurpose.Signing, Cancel);

        Assert.Equal((1, 2), (older.Version, latest.Version));
        Assert.True(Verifies(versions.PublicKeys![1], data, older.Signature));
        Assert.True(Verifies(versions.PublicKeys[2], data, latest.Signature));
        Assert.False(Verifies(versions.PublicKeys[2], data, older.Signature));
    }

    [Fact]
    [Proves("ADR-0054")]
    public async Task AUnitTokenUsesItsOwnTenantsKeysAndCannotRotateThemOrUseAnothers()
    {
        var own = NewPrefix();
        var other = NewPrefix();
        using var root = new OpenBaoSecretStore(openBao.Options());
        using var administration = new TransitKeyService(openBao.Options(), new KeyOptions());
        foreach (var prefix in new[] { own, other })
        {
            foreach (var purpose in KeyPurposes.All)
            {
                await administration.EnsureAsync(KeyPurposes.KeyName(prefix, purpose), KeyKinds.Of(purpose), Cancel);
            }
        }

        var grants = new[]
        {
            new KeyGrant(KeyPurposes.KeyName(own, KeyPurpose.Data), KeyUses.Encrypt | KeyUses.Decrypt | KeyUses.Rewrap | KeyUses.Read),
            new KeyGrant(KeyPurposes.KeyName(own, KeyPurpose.Index), KeyUses.Hash | KeyUses.Read),
        };
        await root.CreateReadPolicyAsync(own, [$"tenants/{own}/unit"], Cancel, keys: grants);
        var token = await root.CreateTokenAsync([own], own, TimeSpan.FromMinutes(5), Cancel);
        using var mine = new TransitKeyService(openBao.Options(token: token), new KeyOptions { Prefix = own });
        using var theirs = new TransitKeyService(openBao.Options(token: token), new KeyOptions { Prefix = other });

        var sealedValue = await mine.EncryptAsync(KeyPurpose.Data, Encoding.UTF8.GetBytes("12-3456789"), Cancel);
        Assert.Equal("12-3456789", Encoding.UTF8.GetString(await mine.DecryptAsync(KeyPurpose.Data, sealedValue, Cancel)));
        await mine.HashAsync(KeyPurpose.Index, Encoding.UTF8.GetBytes("12-3456789"), null, Cancel);
        await Assert.ThrowsAsync<SecretStoreException>(() => theirs.EncryptAsync(KeyPurpose.Data, Encoding.UTF8.GetBytes("12-3456789"), Cancel));
        await Assert.ThrowsAsync<SecretStoreException>(() => theirs.HashAsync(KeyPurpose.Index, Encoding.UTF8.GetBytes("12-3456789"), null, Cancel));
        await Assert.ThrowsAsync<SecretStoreException>(() => mine.RotateAsync(KeyPurposes.KeyName(own, KeyPurpose.Data), Cancel));
        await Assert.ThrowsAsync<SecretStoreException>(() => mine.RetireAsync(KeyPurposes.KeyName(own, KeyPurpose.Data), 1, Cancel));
    }

    [Fact]
    [Proves("ADR-0054")]
    public async Task TheProcessKeysKeepTheTransitEnginesRules()
    {
        using var keys = new LocalKeyService(new KeyOptions { Prefix = "tenant-local", Local = new KeyOptions.LocalKeyOptions { Material = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) } });
        var data = KeyPurposes.KeyName("tenant-local", KeyPurpose.Data);
        var first = await keys.EncryptAsync(KeyPurpose.Data, Encoding.UTF8.GetBytes("secret"), Cancel);

        await keys.RotateAsync(data, Cancel);
        var moved = (await keys.RewrapAsync(KeyPurpose.Data, [first], Cancel))[0];
        await keys.RetireAsync(data, 2, Cancel);
        var signature = await keys.SignAsync(KeyPurpose.Signing, [1, 2, 3], null, Cancel);
        var versions = await keys.VersionsAsync(KeyPurpose.Signing, Cancel);

        Assert.Equal((1, 2), (KeyText.VersionOf(first), KeyText.VersionOf(moved)));
        await Assert.ThrowsAsync<KeyVersionRetiredException>(() => keys.DecryptAsync(KeyPurpose.Data, first, Cancel));
        Assert.Equal("secret", Encoding.UTF8.GetString(await keys.DecryptAsync(KeyPurpose.Data, moved, Cancel)));
        Assert.True(Verifies(versions.PublicKeys![signature.Version], [1, 2, 3], signature.Signature));
    }

    private static string NewPrefix() => $"tenant-k{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";

    private static bool Verifies(string publicKeyPem, byte[] data, byte[] signature)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }
}
