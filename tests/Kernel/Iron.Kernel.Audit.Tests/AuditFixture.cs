using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Events;
using Iron.Kernel.Files;
using Iron.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Kernel.Audit.Tests;

/// <summary>
/// One PostgreSQL container for the test run, with the Audit and Authorization modules migrated and seeded through the
/// unit's own commands, and the app role created and granted, as the fleet does for a tenant (T14); the audit verifier's
/// role too (ADR-0050), and the run's Azurite for the archive.
/// </summary>
public sealed class AuditFixture : IAsyncLifetime
{
    // A role lives in the whole server, which the run's projects share; the fleet's tests make acme_app for their tenant.
    public const string AppRole = "audit_app";
    private const string AppPassword = "audit";

    /// <summary>The tests' tenant as the fleet's verifier names it; its role is <c>auditchain_audit</c> (ADR-0050).</summary>
    public const string ChainTenant = "auditchain";
    public const string ReaderRole = ChainTenant + "_audit";
    public const string ReaderPassword = "reader";

    /// <summary>The tests' container in the run's one Azurite, apart from the other projects'.</summary>
    public const string Container = "audit-tests";

    private TestPostgres? _postgres;
    private TestBlob? _blob;

    public string StorageConnectionString => (_blob ?? throw new InvalidOperationException("The fixture is not initialised.")).ConnectionString;

    /// <summary>The owner connection: migrations, seeds, grants.</summary>
    public string ConnectionString => Postgres.ConnectionString;

    /// <summary>The project's own database on the run's server, or on a server of its own when it runs alone (tests/Iron.Testing).</summary>
    private TestPostgres Postgres => _postgres ?? throw new InvalidOperationException("The fixture is not initialised.");

    /// <summary>The runtime connection: what a unit's hosts use.</summary>
    public string AppConnectionString => new NpgsqlConnectionStringBuilder(ConnectionString) { Username = AppRole, Password = AppPassword }.ToString();

    public async ValueTask InitializeAsync()
    {
        _postgres = await TestPostgres.StartAsync("audit", TestContext.Current.CancellationToken);
        _blob = await TestBlob.StartAsync(TestContext.Current.CancellationToken);
        await using (var dataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build())
        await using (var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"create role {AppRole} login password '{AppPassword}'; create role {ReaderRole} login password '{ReaderPassword}'";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

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

    /// <summary>
    /// A database of its own, migrated with the app and reader roles, for a test that tampers with its chain: the shared
    /// one stays whole for the others. Answers the owner's connection.
    /// </summary>
    public async Task<string> NewChainDatabaseAsync(string scenario)
    {
        var connectionString = await Postgres.CreateDatabaseAsync($"audit_{scenario}_{Guid.NewGuid():N}"[..40], TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), connectionString);
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        return connectionString;
    }

    /// <summary>A connection as another role to the same database.</summary>
    public static string As(string connectionString, string role, string password) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = role, Password = password }.ToString();

    /// <summary>The services a host would have for these two modules, with a controllable clock and principal.</summary>
    public ServiceProvider BuildServices(FakeTimeProvider time, ICurrentPrincipal principal, string? connectionString = null, string container = Container, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(principal);
        services.AddLogging();
        services.AddIronDatabase(connectionString ?? ConnectionString, AppRole, ReaderRole);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = StorageConnectionString,
            ["Storage:Container"] = container,
        }).AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build();
        services.AddModules(configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new FilesModule()]);
        return services.BuildServiceProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class AuditCollection : ICollectionFixture<AuditFixture>
{
    public const string Name = "audit";
}

public sealed class FixedPrincipal(Guid id) : ICurrentPrincipal
{
    public Guid Id => id;
}

/// <summary>A test principal taken from a request header; Identity replaces this with the session principal in T10.</summary>
public sealed class HeaderPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
{
    public const string HeaderName = "X-Test-Principal";

    public Guid Id => Guid.TryParse(accessor.HttpContext?.Request.Headers[HeaderName].ToString(), out var id) ? id : Guid.Empty;
}
