using Iron.Kernel.Security.Secrets;
using Iron.Testing;
using Microsoft.Extensions.Configuration;

namespace Iron.Kernel.Security.Tests;

/// <summary>OpenBao in development mode, as on the laptop platform, with a mount of this project's own (tests/Iron.Testing).</summary>
public sealed class OpenBaoFixture : IAsyncLifetime
{
    private TestOpenBao? _store;

    public string Address => Store.Address;

    public string RootToken => Store.RootToken;

    public string Mount => Store.Mount;

    public string TransitMount => Store.TransitMount;

    public SecretStoreOptions Options(string path = "tenants/acme", string? token = null) => new() { Address = Address, Token = token ?? RootToken, Mount = Store.Mount, Path = path, TransitMount = Store.TransitMount };

    private TestOpenBao Store => _store ?? throw new InvalidOperationException("The fixture is not initialised.");

    public async ValueTask InitializeAsync() => _store = await TestOpenBao.StartAsync("security", TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class OpenBaoCollection : ICollectionFixture<OpenBaoFixture>
{
    public const string Name = "openbao";
}

/// <summary>TF-073: secrets come from the store at start; the environment holds the address, the token and the path only.</summary>
[Collection(OpenBaoCollection.Name)]
public class SecretStoreTests(OpenBaoFixture openBao)
{
    [Fact]
    [Proves("ADR-0016")]
    public async Task WritesReadsAndReportsMissingSecrets()
    {
        using var store = new OpenBaoSecretStore(openBao.Options());

        Assert.Null(await store.ReadAsync("tenants/nobody/unit", TestContext.Current.CancellationToken));

        await store.WriteAsync("tenants/acme/unit", new Dictionary<string, string> { ["ConnectionStrings:Database"] = "Host=db;Database=acme", ["Storage:Container"] = "acme" }, TestContext.Current.CancellationToken);
        var values = await store.ReadAsync("tenants/acme/unit", TestContext.Current.CancellationToken);

        Assert.NotNull(values);
        Assert.Equal("Host=db;Database=acme", values["ConnectionStrings:Database"]);
        Assert.Equal("acme", values["Storage:Container"]);
        Assert.True(await store.IsReachableAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0016")]
    public async Task TheUnitSecretBecomesConfigurationAtStart()
    {
        using var store = new OpenBaoSecretStore(openBao.Options("tenants/bravo"));
        await store.WriteAsync("tenants/bravo/unit", new Dictionary<string, string> { ["ConnectionStrings:Database"] = "Host=db;Database=bravo", ["Keys__Prefix"] = "tenant-bravo" }, TestContext.Current.CancellationToken);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Secrets:Address"] = openBao.Address,
                ["Secrets:Token"] = openBao.RootToken,
                ["Secrets:Mount"] = openBao.Mount,
                ["Secrets:Path"] = "tenants/bravo",
                ["ConnectionStrings:Database"] = "from-the-environment",
            })
            .AddIronSecrets()
            .Build();

        Assert.Equal("Host=db;Database=bravo", configuration.GetConnectionString("Database"));
        Assert.Equal("tenant-bravo", configuration["Keys:Prefix"]);
    }

    [Fact]
    [Proves("ADR-0016")]
    public void WithoutAStoreConfiguredNothingChanges()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = "from-the-environment" })
            .AddIronSecrets()
            .Build();

        Assert.Equal("from-the-environment", configuration.GetConnectionString("Database"));
    }

    [Fact]
    [Proves("ADR-0016")]
    public void AMissingUnitSecretStopsTheHostWithAClearMessage()
    {
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Secrets:Address"] = openBao.Address,
                ["Secrets:Token"] = openBao.RootToken,
                ["Secrets:Mount"] = openBao.Mount,
                ["Secrets:Path"] = "tenants/nobody",
            })
            .AddIronSecrets();

        var exception = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("tenants/nobody/unit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("fleet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0016")]
    public void AnUnreachableStoreStopsTheHostWithTheAddress()
    {
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Secrets:Address"] = "http://localhost:1",
                ["Secrets:Token"] = "whatever",
                ["Secrets:Path"] = "tenants/acme",
            })
            .AddIronSecrets();

        var exception = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("http://localhost:1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0016")]
    public async Task AWrongTokenIsRefusedByTheStore()
    {
        using var store = new OpenBaoSecretStore(new SecretStoreOptions { Address = openBao.Address, Token = "not-the-token", Path = "tenants/acme" });

        await Assert.ThrowsAsync<SecretStoreException>(() => store.WriteAsync("tenants/acme/unit", new Dictionary<string, string> { ["a"] = "b" }, TestContext.Current.CancellationToken));
    }
}
