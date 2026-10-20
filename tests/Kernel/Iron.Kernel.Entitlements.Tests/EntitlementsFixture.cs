using System.Collections.Concurrent;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Entitlements.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace Iron.Kernel.Entitlements.Tests;

/// <summary>A unit's database for the run: Audit, Authorization and Entitlements migrated and seeded, with the fixture module's permissions in the system roles.</summary>
public sealed class EntitlementsFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:19rc1").Build();

    public string ConnectionString => _container.GetConnectionString();

    public static IReadOnlyList<IModule> Modules() => [new AuditModule(), new AuthorizationModule(), new EntitlementsModule(), new Flags.FlagsModule(), new FixtureModule()];

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new ControlledSource(), SystemPrincipal.Instance);
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>The services a unit has, with a controllable clock and source, and a log that keeps what it is told.</summary>
    public ServiceProvider BuildServices(FakeTimeProvider time, IEntitlementSource source, ICurrentPrincipal principal, CapturingLogs? logs = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(principal);
        services.AddSingleton(source);
        services.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
        services.AddLogging(logging => logging.AddProvider(logs ?? new CapturingLogs()));
        services.AddIronDatabase(ConnectionString);
        services.AddModules(new ConfigurationBuilder().Build(), Modules());
        return services.BuildServiceProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class EntitlementsCollection : ICollectionFixture<EntitlementsFixture>
{
    public const string Name = "entitlements";
}

/// <summary>The store as a test drives it: the document it answers, or a failure.</summary>
public sealed class ControlledSource : IEntitlementSource
{
    private int _reads;

    public EntitlementsDocument? Document { get; set; }

    public bool Fails { get; set; }

    public int Reads => Volatile.Read(ref _reads);

    public string Describe() => "the test store";

    public Task<EntitlementsDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        return Fails ? throw new HttpRequestException("The store does not answer.") : Task.FromResult(Document);
    }
}

/// <summary>Documents as the control plane writes them, for the tests.</summary>
public static class Documents
{
    public static readonly DateOnly Start = new(2026, 1, 1);

    public static EntitlementsDocument With(int revision, IReadOnlyList<ModuleEntitlement>? modules = null, IReadOnlyList<FeatureEntitlement>? features = null, int graceDays = 14) =>
        new(EntitlementsDocument.CurrentFormat, "echo", revision, "test", graceDays, DateTimeOffset.UtcNow, modules ?? [], features ?? []);

    public static FeatureEntitlement Feature(string code, EntitlementStatus status = EntitlementStatus.Enabled, DateOnly? endsOn = null) => new(code, status, Start, endsOn);

    public static ModuleEntitlement Fixture(EntitlementStatus status = EntitlementStatus.Enabled, int seats = 2, DateOnly? endsOn = null) =>
        new(FixtureModule.Licence.Code, status, SubscriptionType.PerPerson, seats, Start, endsOn);
}

/// <summary>
/// A per-person module licensed in its own code, as a business or product module will be (TF-041): no such module exists
/// yet, so this one proves the module checks and seats. Its feature <c>fixture.bulk</c> belongs to it.
/// </summary>
public sealed class FixtureModule : IModule
{
    public static readonly ModuleLicence Licence = new("fixture", SubscriptionType.PerPerson);

    public static readonly Feature Bulk = new("fixture.bulk", "Change many fixture things at once");

    public static readonly Permission Read = new("fixture.things.read", "Read fixture things", Risk.Low);

    public static readonly Permission Manage = new("fixture.things.manage", "Change fixture things", Risk.Medium);

    public string Name => "fixture";

    public IReadOnlyList<Permission> Permissions => [Read, Manage];

    ModuleLicence? IModule.Licence => Licence;

    public IReadOnlyList<Feature> Features => [Bulk];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/fixture-things", () => TypedResults.Ok(new { data = Array.Empty<string>() })).RequirePermission(Read);
        endpoints.MapPost("/api/v1/fixture-things", () => TypedResults.Ok(new { made = true })).RequirePermission(Manage);
        endpoints.MapPost("/api/v1/fixture-things/bulk", () => TypedResults.Ok(new { changed = 2 })).RequirePermission(Manage).RequireFeature(Bulk.Code);
    }
}

/// <summary>People and service accounts as Identity would answer for them; a test adds the ones it uses.</summary>
public sealed class TestPrincipals : IPrincipals
{
    private readonly ConcurrentDictionary<Guid, PrincipalSummary> _known = new();

    public Guid Person(string name)
    {
        var id = Ids.New();
        _known[id] = new PrincipalSummary(id, PrincipalKind.User, name, null);
        return id;
    }

    public Guid Service(string name)
    {
        var id = Ids.New();
        _known[id] = new PrincipalSummary(id, PrincipalKind.Service, name, null);
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
