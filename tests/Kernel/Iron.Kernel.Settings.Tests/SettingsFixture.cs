using System.Collections.Concurrent;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Security.Secrets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace Iron.Kernel.Settings.Tests;

/// <summary>A unit's database for the run: Audit, Authorization and Settings migrated and seeded, with the probe module's settings.</summary>
public sealed class SettingsFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:19rc1").Build();

    public string ConnectionString => _container.GetConnectionString();

    public static IReadOnlyList<IModule> Modules() => [new AuditModule(), new AuthorizationModule(), new SettingsModule(), new ProbeModule()];

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// The services a unit has, with a controllable clock, the people the test names, a store that counts its reads, and
    /// a log that keeps what it is told. Another call is another process: it has a cache of its own.
    /// </summary>
    public ServiceProvider BuildServices(FakeTimeProvider time, TestPrincipals? principals = null, ICurrentPrincipal? actor = null, CapturingLogs? logs = null, ISecretStore? store = null, string? connectionString = null, IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(actor ?? SystemPrincipal.Instance);
        services.AddSingleton<IPrincipals>(principals ?? new TestPrincipals());
        services.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
        if (store is not null)
        {
            services.AddSingleton(store);
            services.AddSingleton(new SecretStoreOptions { Address = "http://store.test", Token = "test", Path = "tenants/echo" });
        }

        services.AddLogging(logging => logging.AddProvider(logs ?? new CapturingLogs()));
        services.AddIronDatabase(connectionString ?? ConnectionString);
        services.AddModules(new ConfigurationBuilder().AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()).Build(), Modules());
        return services.BuildServiceProvider();
    }

    /// <summary>A unit under the parent (the root when none), with a code unique to the run.</summary>
    public static async Task<Guid> UnitAsync(IServiceProvider provider, string name, Guid? parent = null)
    {
        await using var scope = provider.CreateAsyncScope();
        var tree = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var parentId = parent ?? (await scope.ServiceProvider.GetRequiredService<IOrganisationUnits>().RootAsync(TestContext.Current.CancellationToken))!.Id;
        var created = await tree.CreateAsync(parentId, name, $"{name.ToLowerInvariant().Replace(' ', '-')}-{Ids.New():N}"[..30], KernelOrganisationKinds.Unit, TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
        return created.Value.Id;
    }
}

[CollectionDefinition(Name)]
public sealed class SettingsCollection : ICollectionFixture<SettingsFixture>
{
    public const string Name = "settings";
}

/// <summary>
/// A module that exists only for these tests: a setting every scope allows, a number with a range, and a secret
/// reference, since no real secret setting exists yet (ADR-0036).
/// </summary>
public sealed class ProbeModule : IModule
{
    public string Name => "probe";

    public IReadOnlyList<Permission> Permissions => [];

    public IReadOnlyList<SettingDefinition> Settings => ProbeSettings.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

public static class ProbeSettings
{
    public static readonly Setting<string> Tone = Setting.Choice("probe.reply.tone", "How the probe answers: plain, warm or brief.", "plain", ["plain", "warm", "brief"], SettingScopes.Tenant | SettingScopes.Unit | SettingScopes.User, visibleToShell: true);

    public static readonly Setting<int> Batch = Setting.WholeNumber("probe.limits.batch", "How many rows the probe takes at once.", 50, 10, 500, SettingScopes.Tenant | SettingScopes.Unit);

    public static readonly Setting<SecretReference?> MailPassword = Setting.Secret("probe.mail.password", "The password the probe's mail account signs in with.", SettingScopes.Tenant);

    public static IReadOnlyList<SettingDefinition> All => [Tone, Batch, MailPassword];
}

/// <summary>People and service accounts as Identity would answer for them, with their home units.</summary>
public sealed class TestPrincipals : IPrincipals
{
    private readonly ConcurrentDictionary<Guid, PrincipalSummary> _known = new();

    public Guid Add(string name, Guid? homeUnit = null)
    {
        var id = Ids.New();
        _known[id] = new PrincipalSummary(id, PrincipalKind.User, name, homeUnit);
        return id;
    }

    public Task<IReadOnlyDictionary<Guid, PrincipalSummary>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PrincipalSummary>>(ids.Where(_known.ContainsKey).ToDictionary(id => id, id => _known[id]));

    public Task<IReadOnlyList<Guid>> HomedAtAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. _known.Values.Where(p => p.HomeUnitId is { } unit && unitIds.Contains(unit)).Select(p => p.Id)]);
}

/// <summary>A principal the test sets: who saves a value.</summary>
public sealed class FixedPrincipal(Guid id) : ICurrentPrincipal
{
    public Guid Id { get; set; } = id;
}

/// <summary>A test principal taken from a request header; Identity provides the real one.</summary>
public sealed class HeaderPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
{
    public const string HeaderName = "X-Test-Principal";

    public Guid Id => Guid.TryParse(accessor.HttpContext?.Request.Headers[HeaderName].ToString(), out var id) ? id : Guid.Empty;
}

/// <summary>A secret store in memory that counts its reads, so a test sees when the store was asked.</summary>
public sealed class CountingStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _secrets = new(StringComparer.Ordinal);
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    public void Put(string path, string value) => _secrets[path] = new Dictionary<string, string> { [SecretReference.ValueKey] = value };

    public Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        return Task.FromResult(_secrets.TryGetValue(path, out var values) ? values : null);
    }

    public Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        _secrets[path] = values;
        return Task.CompletedTask;
    }

    public Task<bool> IsReachableAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}

/// <summary>Keeps every log line with its level, so a test can see the alert.</summary>
public sealed class CapturingLogs : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue((logLevel, category, formatter(state, exception)));
    }
}
