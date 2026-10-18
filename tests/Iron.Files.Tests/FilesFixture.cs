using Iron.Api.Modules;
using Iron.Audit;
using Iron.Authorization;
using Iron.Data;
using Iron.Data.Commands;
using Iron.Kernel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.Azurite;
using Testcontainers.PostgreSql;

namespace Iron.Files.Tests;

/// <summary>PostgreSQL and Azurite for the test run, migrated and seeded through the unit's own commands (the seed creates the container).</summary>
public sealed class FilesFixture : IAsyncLifetime
{
    public const string Container = "acme";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:19rc1").Build();
    // --skipApiVersionCheck as on the laptop platform: the SDK's newest API version runs ahead of the emulator (ADR-0006).
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0").WithCommand("--skipApiVersionCheck", "--loose").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public string StorageConnectionString => _azurite.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(TestContext.Current.CancellationToken), _azurite.StartAsync(TestContext.Current.CancellationToken));
        await using var provider = BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        await _azurite.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public ServiceProvider BuildServices(FakeTimeProvider time, ICurrentPrincipal principal, string container = Container)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = StorageConnectionString,
            ["Storage:Container"] = container,
            ["Storage:MaxUploadMegabytes"] = "1",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(principal);
        services.AddLogging();
        services.AddIronDatabase(ConnectionString);
        services.AddModules(configuration, [new AuditModule(), new AuthorizationModule(), new FilesModule()]);
        return services.BuildServiceProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class FilesCollection : ICollectionFixture<FilesFixture>
{
    public const string Name = "files";
}

public sealed class FixedPrincipal(Guid id) : ICurrentPrincipal
{
    public Guid Id => id;
}

/// <summary>A test principal taken from a request header; Identity provides the real one.</summary>
public sealed class HeaderPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
{
    public const string HeaderName = "X-Test-Principal";

    public Guid Id => Guid.TryParse(accessor.HttpContext?.Request.Headers[HeaderName].ToString(), out var id) ? id : Guid.Empty;
}
