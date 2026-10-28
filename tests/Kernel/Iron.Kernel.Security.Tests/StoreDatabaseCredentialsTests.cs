using Iron.Kernel.Security.Secrets;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Security.Tests;

/// <summary>
/// The unit's database password from its secret (TF-073 AC2, ADR-0054): read again after its interval or a refusal, and
/// only for the login the unit secret names, so the fleet's image commands keep the owner's password they were given.
/// </summary>
[Collection(OpenBaoCollection.Name)]
public class StoreDatabaseCredentialsTests(OpenBaoFixture openBao)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-073 AC2", "ADR-0054")]
    public async Task ARotatedPasswordIsReadFromTheUnitSecretAfterItsIntervalOrAtOnceAfterARefusal()
    {
        var path = $"tenants/rotate{Ids.New():N}";
        using var store = new OpenBaoSecretStore(openBao.Options(path));
        await store.WriteAsync($"{path}/unit", Unit("acme_app", "first"), Cancel);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var credentials = new StoreDatabaseCredentials(store, openBao.Options(path), "Host=postgres;Database=acme;Username=acme_app;Password=first", time, TimeSpan.FromMinutes(1));

        var first = await credentials.PasswordAsync(Cancel);
        await store.WriteAsync($"{path}/unit", Unit("acme_app", "second"), Cancel);
        var kept = await credentials.PasswordAsync(Cancel);
        credentials.Invalidate();
        var afterRefusal = await credentials.PasswordAsync(Cancel);
        await store.WriteAsync($"{path}/unit", Unit("acme_app", "third"), Cancel);
        time.Advance(TimeSpan.FromMinutes(1));
        var afterInterval = await credentials.PasswordAsync(Cancel);

        Assert.True(credentials.Rotates);
        Assert.Equal(("first", "first", "second", "third"), (first, kept, afterRefusal, afterInterval));
    }

    [Fact]
    [Proves("ADR-0054")]
    public async Task AnImageCommandConnectingAsTheOwnerKeepsThePasswordItWasGiven()
    {
        var path = $"tenants/owner{Ids.New():N}";
        using var store = new OpenBaoSecretStore(openBao.Options(path));
        await store.WriteAsync($"{path}/unit", Unit("acme_app", "app-password"), Cancel);
        var credentials = new StoreDatabaseCredentials(store, openBao.Options(path), "Host=postgres;Database=acme;Username=acme_owner;Password=owner-password", TimeProvider.System, TimeSpan.FromMinutes(1));

        Assert.Null(await credentials.PasswordAsync(Cancel));
    }

    private static Dictionary<string, string> Unit(string login, string password) =>
        new(StringComparer.Ordinal) { [StoreDatabaseCredentials.ConnectionStringKey] = $"Host=postgres;Port=5432;Database=acme;Username={login};Password={password}" };
}
