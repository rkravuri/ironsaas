using System.Collections.Concurrent;
using System.Security.Cryptography;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Events.Data;
using Iron.Kernel.Events.Services;
using Iron.Kernel.Files.Contracts;
using Iron.Kernel.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Kernel.Events.Tests;

/// <summary>A PostgreSQL server for the module's tests; each test takes a tenant of its own, so events never meet.</summary>
public sealed class EventsFixture : IAsyncLifetime
{
    private TestPostgres? _postgres;

    public async ValueTask InitializeAsync() => _postgres = await TestPostgres.StartAsync("events", TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>A tenant of its own: a database migrated and seeded as the unit's commands do it.</summary>
    public async Task<EventsTenant> NewTenantAsync()
    {
        var name = $"events{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var connectionString = await (_postgres ?? throw new InvalidOperationException("The fixture is not initialised.")).CreateDatabaseAsync(name, TestContext.Current.CancellationToken);
        var tenant = new EventsTenant(connectionString);
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
        return tenant;
    }
}

[CollectionDefinition(Name)]
public sealed class EventsCollection : ICollectionFixture<EventsFixture>
{
    public const string Name = "events";
}

/// <summary>A test tenant: its database, the probe's behaviour, the archive's container, and the dispatcher's settings.</summary>
public sealed class EventsTenant(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public ProbeBehaviour Behaviour { get; } = new();

    public MemoryStorage Storage { get; } = new();

    public Dictionary<string, string?> Configuration { get; } = new(StringComparer.Ordinal)
    {
        ["Events:Dispatch:Attempts"] = "6",
        ["Events:Dispatch:FirstRetrySeconds"] = "2",
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
        services.AddSingleton<IObjectStorage>(Storage);
        services.AddModules(configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new SettingsModule(), new ProbeModule()]);

        // The worker's services; a built provider starts no hosted service, so the tests run each pass themselves.
        services.AddEventDispatch();
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>Publishes in a unit of work of its own, as a module's change does, and commits.</summary>
    public static async Task<Guid> PublishAsync<TData>(IServiceProvider provider, EventType<TData> type, Guid recordId, TData data)
        where TData : class
    {
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginAsync(TestContext.Current.CancellationToken);
        var id = await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(type, recordId, data, TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>One pass of the dispatcher.</summary>
    public static Task<DispatchPass> PassAsync(IServiceProvider provider) => provider.GetRequiredService<EventDispatcher>().RunOnceAsync(TestContext.Current.CancellationToken);

    /// <summary>Passes until one does nothing, at most <paramref name="limit"/>.</summary>
    public static async Task<IReadOnlyList<DispatchPass>> DrainAsync(IServiceProvider provider, int limit = 50)
    {
        var passes = new List<DispatchPass>();
        for (var i = 0; i < limit; i++)
        {
            var pass = await PassAsync(provider);
            passes.Add(pass);
            if (!pass.DidWork)
            {
                break;
            }
        }

        return passes;
    }

    public static async Task<List<InboxEntry>> InboxAsync(IServiceProvider provider, Guid eventId)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EventsDbContext>().Inbox.AsNoTracking().Where(i => i.EventId == eventId).OrderBy(i => i.Subscriber).ToListAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<int> OutboxCountAsync(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EventsDbContext>().Outbox.CountAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>How many audit events of this type the trail holds for the record: a handler's effects, which commit with it or not at all.</summary>
    public async Task<long> AuditCountAsync(string action, Guid recordId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select count(*) from kernel_audit.events where event_type = @action and record_id = @record", connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("record", recordId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? 0L);
    }

    /// <summary>The operator an audit event of the record names in its details, as the unit's commands write it.</summary>
    public async Task<string?> AuditOperatorAsync(string action, Guid recordId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select details->>'operator' from kernel_audit.events where event_type = @action and record_id = @record", connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("record", recordId);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string;
    }
}

public sealed class FixedPrincipal(Guid id) : ICurrentPrincipal
{
    public Guid Id => id;
}

/// <summary>The archive's container, in memory.</summary>
public sealed class MemoryStorage : IObjectStorage
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        Objects[key] = buffer.ToArray();
    }

    public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Objects.TryGetValue(key, out var bytes) ? new StoredObject(new MemoryStream(bytes), "application/octet-stream", bytes.Length) : null);

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Objects.TryRemove(key, out _));

    public Task EnsureContainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
