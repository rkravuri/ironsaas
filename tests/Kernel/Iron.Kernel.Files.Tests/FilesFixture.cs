using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Files.Tests;

/// <summary>PostgreSQL and Azurite for the test run, migrated and seeded through the unit's own commands (the seed creates the container).</summary>
public sealed class FilesFixture : IAsyncLifetime
{
    // Apart from the fleet tests' tenant slugs, which name their containers in the run's one Azurite.
    public const string Container = "files";

    private TestPostgres? _postgres;
    private TestBlob? _blob;

    public string ConnectionString => (_postgres ?? throw new InvalidOperationException("The fixture is not initialised.")).ConnectionString;

    public string StorageConnectionString => (_blob ?? throw new InvalidOperationException("The fixture is not initialised.")).ConnectionString;

    public async ValueTask InitializeAsync()
    {
        _postgres = await TestPostgres.StartAsync("files", TestContext.Current.CancellationToken);
        _blob = await TestBlob.StartAsync(TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        if (_blob is not null)
        {
            await _blob.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
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
