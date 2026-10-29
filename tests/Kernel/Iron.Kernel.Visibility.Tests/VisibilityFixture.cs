using System.Collections.Concurrent;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Identity.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Visibility.Tests;

/// <summary>A unit's database for the run: Audit, Authorization and Visibility migrated and seeded.</summary>
public sealed class VisibilityFixture : IAsyncLifetime
{
    private TestPostgres? _postgres;

    public string ConnectionString => Postgres.ConnectionString;

    /// <summary>The project's own database on the run's server, or on a server of its own when it runs alone (tests/Iron.Testing).</summary>
    private TestPostgres Postgres => _postgres ?? throw new InvalidOperationException("The fixture is not initialised.");

    public static IReadOnlyList<IModule> Modules() => [new AuditModule(), new AuthorizationModule(), new VisibilityModule()];

    public async ValueTask InitializeAsync()
    {
        _postgres = await TestPostgres.StartAsync("visibility", TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new TestPrincipals(), SystemPrincipal.Instance);
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

    /// <summary>The services a unit has, the people the test names, the actor, and what the security activity was told.</summary>
    public ServiceProvider BuildServices(TestPrincipals principals, ICurrentPrincipal actor, NotedActivity? activity = null, IActingFor? acting = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
        services.AddSingleton(actor);
        services.AddSingleton<IPrincipals>(principals);
        services.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
        services.AddLogging();
        services.AddIronDatabase(ConnectionString);
        services.AddModules(new ConfigurationBuilder().Build(), Modules());
        services.AddSingleton<ISecurityActivity>(activity ?? new NotedActivity());
        if (acting is not null)
        {
            services.AddSingleton(acting);
        }

        return services.BuildServiceProvider();
    }

    /// <summary>A code no other test of the run uses: the prefix and the random end of a new id, whose start is its time.</summary>
    public static string Code(string prefix) => $"{prefix}-{Ids.New().ToString("N")[^12..]}".ToUpperInvariant();
}

[CollectionDefinition(Name)]
public sealed class VisibilityCollection : ICollectionFixture<VisibilityFixture>
{
    public const string Name = "visibility";
}

/// <summary>People and service accounts as Identity would answer for them, with their home units.</summary>
public sealed class TestPrincipals : IPrincipals
{
    private readonly ConcurrentDictionary<Guid, PrincipalSummary> _known = new();

    public Guid Add(string name, PrincipalKind type = PrincipalKind.User, Guid? homeUnit = null)
    {
        var id = Ids.New();
        _known[id] = new PrincipalSummary(id, type, name, homeUnit);
        return id;
    }

    public Task<IReadOnlyDictionary<Guid, PrincipalSummary>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PrincipalSummary>>(ids.Where(_known.ContainsKey).ToDictionary(id => id, id => _known[id]));

    public Task<IReadOnlyList<Guid>> HomedAtAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. _known.Values.Where(p => p.HomeUnitId is { } unit && unitIds.Contains(unit)).Select(p => p.Id)]);
}

/// <summary>A principal the test sets.</summary>
public sealed class FixedPrincipal(Guid id) : ICurrentPrincipal
{
    public Guid Id { get; } = id;
}

/// <summary>What Identity's security activity would note, kept in memory.</summary>
public sealed class NotedActivity : ISecurityActivity
{
    public ConcurrentQueue<(Guid PrincipalId, string Kind)> Notes { get; } = new();

    public Task NoteAsync(Guid principalId, string kind, CancellationToken cancellationToken)
    {
        Notes.Enqueue((principalId, kind));
        return Task.CompletedTask;
    }
}

/// <summary>Acting for someone as Authorization would answer it: this delegate acts for that delegator.</summary>
public sealed class FixedActing(Guid delegateId, Guid delegatorId) : IActingFor
{
    public Task<Guid?> DelegatorOfAsync(Guid principalId, CancellationToken cancellationToken) =>
        Task.FromResult<Guid?>(principalId == delegateId ? delegatorId : null);
}
