using Iron.Kernel.Data.Tests.Sample;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Iron.Kernel.Data.Tests;

/// <summary>
/// One PostgreSQL container for the whole test run, with the sample schema created from the sample context.
/// Tests that need it join the "postgres" collection.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:19rc1").Build();

    private NpgsqlDataSource? _dataSource;

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("The fixture is not initialised.");

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        _dataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();

        await using var provider = BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>The services a host would register for the data layer, with a controllable clock and principal.</summary>
    public ServiceProvider BuildServices(TimeProvider time, ICurrentPrincipal principal, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(time);
        services.AddSingleton(principal);
        services.AddIronDatabase(ConnectionString);
        // The sample context is created with EnsureCreated and has no migrations; the migrate command must still run over it.
        services.AddIronDbContext<SampleDbContext>(options => options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

public sealed class FixedPrincipal(Guid id) : ICurrentPrincipal
{
    public Guid Id => id;
}
