using System.Security.Cryptography;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Events;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Jobs.Data;
using Iron.Kernel.Jobs.Services;
using Iron.Kernel.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Kernel.Jobs.Tests;

/// <summary>A PostgreSQL server for the module's tests; each test takes a tenant of its own, so runs never meet.</summary>
public sealed class JobsFixture : IAsyncLifetime
{
    private TestPostgres? _postgres;

    public async ValueTask InitializeAsync() => _postgres = await TestPostgres.StartAsync("jobs", TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>A tenant of its own: a database migrated and seeded as the unit's commands do it.</summary>
    public async Task<JobsTenant> NewTenantAsync(bool withSuite = false)
    {
        var name = $"jobs{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var connectionString = await (_postgres ?? throw new InvalidOperationException("The fixture is not initialised.")).CreateDatabaseAsync(name, TestContext.Current.CancellationToken);
        var tenant = new JobsTenant(connectionString, name) { RunsProbeSuite = withSuite };
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
        return tenant;
    }
}

[CollectionDefinition(Name)]
public sealed class JobsCollection : ICollectionFixture<JobsFixture>
{
    public const string Name = "jobs";
}

/// <summary>Whether the tenant is suspended, as a test sets it: the entitlements document's word, in the unit (TF-122 AC3).</summary>
public sealed class ProbeStanding : ITenantStanding
{
    public bool Suspended { get; set; }
}

/// <summary>A test tenant: its database, the probe's behaviour and standing, and the runner's settings. Each provider is one worker process.</summary>
public sealed class JobsTenant(string connectionString, string database)
{
    public string ConnectionString { get; } = connectionString;

    public string Database { get; } = database;

    public ProbeJobBehaviour Behaviour { get; } = new();

    public ProbeStanding Standing { get; } = new();

    public bool RunsProbeSuite { get; set; }

    public Dictionary<string, string?> Configuration { get; } = new(StringComparer.Ordinal)
    {
        ["Jobs:Runner:Slots"] = "4",
        ["Jobs:Runner:LeaseGraceSeconds"] = "30",
    };

    public ServiceProvider Build(TimeProvider time, Guid? principal = null, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(time);
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<ICurrentPrincipal>(new FixedPrincipal(principal ?? SystemPrincipal.SystemId));
        services.AddSingleton(new TenantContext(TenantId.Parse("acme"), ["acme.localhost"]));
        services.AddIronDatabase(ConnectionString);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Configuration).Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(Behaviour);
        services.AddSingleton<ITenantStanding>(Standing);
        IReadOnlyList<SuiteDefinition> suites = RunsProbeSuite ? [ProbeSuiteJobsModule.Definition()] : [];
        services.AddModules(configuration, InstalledSuites.Compose([new AuditModule(), new EventsModule(), new AuthorizationModule(), new SettingsModule(), new JobsModule(), new ProbeJobsModule()], suites), suites);
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public static JobRunner Runner(IServiceProvider provider) => provider.GetRequiredService<JobRunner>();

    /// <summary>Ticks until a tick does nothing, each with the runs it started finished, at most <paramref name="limit"/>.</summary>
    public static async Task<IReadOnlyList<RunnerTick>> DrainAsync(IServiceProvider provider, int limit = 20)
    {
        var ticks = new List<RunnerTick>();
        for (var i = 0; i < limit; i++)
        {
            var tick = await Runner(provider).RunOnceAsync(TestContext.Current.CancellationToken);
            ticks.Add(tick);
            if (!tick.DidWork)
            {
                break;
            }
        }

        return ticks;
    }

    /// <summary>Starts a run of a job in a unit of work of its own, as a module's change does.</summary>
    public static async Task<Guid> StartAsync(IServiceProvider provider, JobDefinition job, string key, string? payload = null)
    {
        await using var scope = provider.CreateAsyncScope();
        await using var transaction = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(TestContext.Current.CancellationToken);
        var id = await scope.ServiceProvider.GetRequiredService<IJobStarter>().StartAsync(job, key, payload, TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>Publishes in a unit of work of its own, as a module's change does, and commits.</summary>
    public static async Task<Guid> PublishAsync<TData>(IServiceProvider provider, EventType<TData> type, Guid recordId, TData data)
        where TData : class
    {
        await using var scope = provider.CreateAsyncScope();
        await using var transaction = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(TestContext.Current.CancellationToken);
        var id = await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(type, recordId, data, TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return id;
    }

    public static async Task<List<JobRun>> RunsAsync(IServiceProvider provider, string job)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobsDbContext>().Runs.AsNoTracking().Where(r => r.Job == job).OrderBy(r => r.ScheduledFor).ThenBy(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<JobRun> RunAsync(IServiceProvider provider, Guid id)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobsDbContext>().Runs.AsNoTracking().SingleAsync(r => r.Id == id, TestContext.Current.CancellationToken);
    }

    public static async Task<JobRecord> RecordAsync(IServiceProvider provider, string job)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobsDbContext>().Jobs.AsNoTracking().SingleAsync(j => j.Name == job, TestContext.Current.CancellationToken);
    }

    /// <summary>How many audit events of this type the trail holds for the target (a job's name).</summary>
    public async Task<long> AuditCountAsync(string action, string target)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select count(*) from kernel_audit.events where event_type = @action and target = @target", connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("target", target);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? 0L);
    }

    public async Task<string?> AuditDetailAsync(string action, string target, string key)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select details->>@key from kernel_audit.events where event_type = @action and target = @target order by created_at desc limit 1", connection);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("target", target);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string;
    }
}

public sealed class FixedPrincipal(Guid id) : ICurrentPrincipal
{
    public Guid Id => id;
}
