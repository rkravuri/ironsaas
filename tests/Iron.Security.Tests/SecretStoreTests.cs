using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Iron.Security.Secrets;
using Microsoft.Extensions.Configuration;

namespace Iron.Security.Tests;

/// <summary>An OpenBao container in development mode, as on the laptop platform.</summary>
public sealed class OpenBaoFixture : IAsyncLifetime
{
    public const string RootToken = "test-root-token";

    private readonly IContainer _container = new ContainerBuilder("openbao/openbao:2.7.1")
        .WithCommand("server", "-dev", "-dev-root-token-id=" + RootToken, "-dev-listen-address=0.0.0.0:8200")
        .WithEnvironment("BAO_ADDR", "http://127.0.0.1:8200")
        .WithCreateParameterModifier(parameters =>
        {
            parameters.HostConfig ??= new Docker.DotNet.Models.HostConfig();
            parameters.HostConfig.CapAdd = ["IPC_LOCK"];
        })
        .WithPortBinding(8200, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8200).ForPath("/v1/sys/health")))
        .Build();

    public string Address => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8200)}";

    public SecretStoreOptions Options(string path = "tenants/acme") => new() { Address = Address, Token = RootToken, Path = path };

    public async ValueTask InitializeAsync() => await _container.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
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
    public async Task TheUnitSecretBecomesConfigurationAtStart()
    {
        using var store = new OpenBaoSecretStore(openBao.Options("tenants/bravo"));
        await store.WriteAsync("tenants/bravo/unit", new Dictionary<string, string> { ["ConnectionStrings:Database"] = "Host=db;Database=bravo", ["Tokens__SigningKeyPem"] = "-----BEGIN EC PRIVATE KEY-----" }, TestContext.Current.CancellationToken);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Secrets:Address"] = openBao.Address,
                ["Secrets:Token"] = OpenBaoFixture.RootToken,
                ["Secrets:Path"] = "tenants/bravo",
                ["ConnectionStrings:Database"] = "from-the-environment",
            })
            .AddIronSecrets()
            .Build();

        Assert.Equal("Host=db;Database=bravo", configuration.GetConnectionString("Database"));
        Assert.Equal("-----BEGIN EC PRIVATE KEY-----", configuration["Tokens:SigningKeyPem"]);
    }

    [Fact]
    public void WithoutAStoreConfiguredNothingChanges()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = "from-the-environment" })
            .AddIronSecrets()
            .Build();

        Assert.Equal("from-the-environment", configuration.GetConnectionString("Database"));
    }

    [Fact]
    public void AMissingUnitSecretStopsTheHostWithAClearMessage()
    {
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Secrets:Address"] = openBao.Address,
                ["Secrets:Token"] = OpenBaoFixture.RootToken,
                ["Secrets:Path"] = "tenants/nobody",
            })
            .AddIronSecrets();

        var exception = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("tenants/nobody/unit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("fleet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
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
    public async Task AWrongTokenIsRefusedByTheStore()
    {
        using var store = new OpenBaoSecretStore(new SecretStoreOptions { Address = openBao.Address, Token = "not-the-token", Path = "tenants/acme" });

        await Assert.ThrowsAsync<SecretStoreException>(() => store.WriteAsync("tenants/acme/unit", new Dictionary<string, string> { ["a"] = "b" }, TestContext.Current.CancellationToken));
    }
}
