using Iron.Kernel;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Iron.Testing;

/// <summary>
/// A PostgreSQL database of the test project's own. Under make test the run's one server holds every project's database
/// (<see cref="SharedVariable"/> holds its superuser connection); a project run alone starts a server of its own. Either
/// way the connection is a superuser's, as migrations, grants and the fleet's database step need. Names that live in the
/// whole server, not in a database (roles), must stay apart between projects: the fleet's are its tenants'.
/// </summary>
public sealed class TestPostgres : IAsyncDisposable
{
    public const string Image = "postgres:19rc1";

    /// <summary>The run's server, as make test started it: a superuser connection string to its <c>postgres</c> database.</summary>
    public const string SharedVariable = "IRON_TEST_POSTGRES";

    private readonly PostgreSqlContainer? _container;
    private readonly string _server;

    private TestPostgres(PostgreSqlContainer? container, string server, string database)
    {
        _container = container;
        _server = server;
        ConnectionString = WithDatabase(database);
        var builder = new NpgsqlConnectionStringBuilder(server);
        Host = builder.Host ?? "localhost";
        Port = builder.Port;
    }

    /// <summary>The project's own database.</summary>
    public string ConnectionString { get; }

    public string Host { get; }

    public int Port { get; }

    /// <summary>A new database for the project on the run's server (or its own), named after the project and a fresh id.</summary>
    public static async Task<TestPostgres> StartAsync(string project, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        PostgreSqlContainer? container = null;
        var server = Environment.GetEnvironmentVariable(SharedVariable);
        if (string.IsNullOrWhiteSpace(server))
        {
            container = new PostgreSqlBuilder(Image).Build();
            await container.StartAsync(cancellationToken).ConfigureAwait(false);
            server = container.GetConnectionString();
        }

        var postgres = new TestPostgres(container, server, $"{project}_{Ids.New():N}");
        await postgres.CreateDatabaseAsync(new NpgsqlConnectionStringBuilder(postgres.ConnectionString).Database!, cancellationToken).ConfigureAwait(false);
        return postgres;
    }

    /// <summary>Another database on the same server, such as a second tenant's; answers its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var connection = new NpgsqlConnection(_server);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var create = new NpgsqlCommand($"create database \"{name}\"", connection);
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return WithDatabase(name);
    }

    public async ValueTask DisposeAsync()
    {
        // The run's server goes when the run ends (tests/test-services.sh); only a server of the project's own goes here.
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private string WithDatabase(string database) => new NpgsqlConnectionStringBuilder(_server) { Database = database }.ConnectionString;
}
