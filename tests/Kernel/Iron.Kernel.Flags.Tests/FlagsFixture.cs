using System.Collections.Concurrent;
using System.Text.Json;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Events;
using Iron.Kernel.Flags.Contracts;
using Iron.Kernel.Flags.Services;
using Iron.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Flags.Tests;

/// <summary>A unit's database for the run: Audit, Authorization and Flags migrated and seeded.</summary>
public sealed class FlagsFixture : IAsyncLifetime
{
    private TestPostgres? _postgres;

    public string ConnectionString => Postgres.ConnectionString;

    /// <summary>The project's own database on the run's server, or on a server of its own when it runs alone (tests/Iron.Testing).</summary>
    private TestPostgres Postgres => _postgres ?? throw new InvalidOperationException("The fixture is not initialised.");

    public static IReadOnlyList<IModule> Modules() => [new AuditModule(), new EventsModule(), new AuthorizationModule(), new FlagsModule()];

    public async ValueTask InitializeAsync()
    {
        _postgres = await TestPostgres.StartAsync("flags", TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new ControlledSource(), SystemPrincipal.Instance);
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>The services a unit has, with a controllable clock and source, and a log that keeps what it is told.</summary>
    public ServiceProvider BuildServices(FakeTimeProvider time, IFlagRuleSource source, ICurrentPrincipal principal, CapturingLogs? logs = null, IPrincipals? principals = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(principal);
        services.AddSingleton(source);
        services.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
        if (principals is not null)
        {
            services.AddSingleton(principals);
        }

        services.AddLogging(logging => logging.AddProvider(logs ?? new CapturingLogs()));
        services.AddIronDatabase(ConnectionString);
        services.AddModules(new ConfigurationBuilder().Build(), Modules());
        return services.BuildServiceProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class FlagsCollection : ICollectionFixture<FlagsFixture>
{
    public const string Name = "flags";
}

/// <summary>The store as a test drives it: the rules of both paths it answers, or a failure.</summary>
public sealed class ControlledSource : IFlagRuleSource
{
    private int _reads;

    public FlagRuleSet Rules { get; set; } = new(null, null);

    public bool Fails { get; set; }

    public int Reads => Volatile.Read(ref _reads);

    public string Describe() => "the test store";

    public Task<FlagRuleSet> ReadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        return Fails ? throw new HttpRequestException("The store does not answer.") : Task.FromResult(Rules);
    }
}

/// <summary>Rules and documents as the control plane writes them, for the tests.</summary>
public static class Rules
{
    public static readonly DateTimeOffset Changed = new(2026, 10, 20, 8, 30, 0, TimeSpan.Zero);

    public static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value);

    public static FlagRuleEntry Global(string flag, object value, int position = 0, FlagCondition? condition = null, string? reason = null) =>
        new(Ids.New(), flag, FlagScope.Global, null, null, null, null, condition ?? FlagCondition.Always, Value(value), position, reason, Changed);

    public static FlagRuleEntry Region(string flag, string region, object value, int position = 0) =>
        new(Ids.New(), flag, FlagScope.Region, region, null, null, null, FlagCondition.Always, Value(value), position, null, Changed);

    public static FlagRuleEntry Tenant(string flag, string tenant, object value, int position = 0, FlagCondition? condition = null, string? reason = null) =>
        new(Ids.New(), flag, FlagScope.Tenant, null, tenant, null, null, condition ?? FlagCondition.Always, Value(value), position, reason, Changed);

    public static FlagRuleEntry Unit(string flag, string tenant, Guid unit, object value, int position = 0) =>
        new(Ids.New(), flag, FlagScope.Unit, null, tenant, unit, null, FlagCondition.Always, Value(value), position, null, Changed);

    public static FlagRuleEntry User(string flag, string tenant, Guid user, object value) =>
        new(Ids.New(), flag, FlagScope.User, null, tenant, null, user, FlagCondition.Always, Value(value), 0, null, Changed);

    public static FlagRuleSet Set(int platformRevision, IReadOnlyList<FlagRuleEntry> platform, int tenantRevision = 0, IReadOnlyList<FlagRuleEntry>? tenant = null, IReadOnlyDictionary<string, string>? attributes = null) =>
        new(
            new FlagRulesDocument(FlagRulesDocument.CurrentFormat, FlagRulesDocument.PlatformPath, platformRevision, Changed, null, platform),
            new FlagRulesDocument(FlagRulesDocument.CurrentFormat, FlagRulesDocument.TenantPath("echo"), tenantRevision, Changed, attributes, tenant ?? []));
}

/// <summary>People and service accounts as Identity would answer for them, with their home units.</summary>
public sealed class TestPrincipals : IPrincipals
{
    private readonly ConcurrentDictionary<Guid, PrincipalSummary> _known = new();

    public Guid Add(PrincipalKind kind, string name, Guid? homeUnit = null)
    {
        var id = Ids.New();
        _known[id] = new PrincipalSummary(id, kind, name, homeUnit);
        return id;
    }

    public Task<IReadOnlyDictionary<Guid, PrincipalSummary>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PrincipalSummary>>(ids.Where(_known.ContainsKey).ToDictionary(id => id, id => _known[id]));

    public Task<IReadOnlyList<Guid>> HomedAtAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. _known.Values.Where(p => p.HomeUnitId is { } unit && unitIds.Contains(unit)).Select(p => p.Id)]);
}

/// <summary>A test principal taken from a request header; Identity provides the real one.</summary>
public sealed class HeaderPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
{
    public const string HeaderName = "X-Test-Principal";

    public Guid Id => Guid.TryParse(accessor.HttpContext?.Request.Headers[HeaderName].ToString(), out var id) ? id : Guid.Empty;
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
