using System.Collections.Concurrent;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>One PostgreSQL container for the test run, migrated and seeded with the module's own commands.</summary>
public sealed class AuthorizationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:19rc1").Build();

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>The principals Identity would answer for; a test adds the ones it grants roles to.</summary>
    public TestPrincipals Principals { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), new RecordingAuditWriter());
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>A second tenant's database in the same container, migrated and seeded: export and import cross tenants (TF-032).</summary>
    public async Task<string> CreateTenantDatabaseAsync(string name)
    {
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"create database \"{name}\"", connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
        await using var provider = BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), new RecordingAuditWriter(), connectionString);
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
        return connectionString;
    }

    /// <summary>A scope of the module's services acting as one principal; disposing it disposes its provider and closes its connections.</summary>
    public TestScope Scope(Guid principal, RecordingAuditWriter? audit = null, string? connectionString = null)
    {
        audit ??= new RecordingAuditWriter();
        return new TestScope(BuildServices(new FakeTimeProvider(), new FixedPrincipal(principal), audit, connectionString), audit);
    }

    /// <summary>A scope of a principal acting for a delegator (ADR-0037), on the given clock.</summary>
    public TestScope ActingScope(Guid principal, Guid actingFor, FakeTimeProvider? time = null)
    {
        var audit = new RecordingAuditWriter();
        return new TestScope(BuildServices(time ?? new FakeTimeProvider(), new FixedPrincipal(principal, actingFor), audit), audit);
    }

    /// <summary>A scope of a principal on the given clock.</summary>
    public TestScope Scope(Guid principal, FakeTimeProvider time)
    {
        var audit = new RecordingAuditWriter();
        return new TestScope(BuildServices(time, new FixedPrincipal(principal), audit), audit);
    }

    /// <summary>The services a host would have for this module, with a controllable clock, principal and audit writer.</summary>
    public ServiceProvider BuildServices(FakeTimeProvider time, ICurrentPrincipal principal, RecordingAuditWriter audit, string? connectionString = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(principal);
        services.AddSingleton<IAuditWriter>(audit);
        services.AddSingleton<IPrincipals>(Principals);
        services.AddLogging();
        services.AddIronDatabase(connectionString ?? ConnectionString);
        services.AddModules(new ConfigurationBuilder().Build(), [new AuthorizationModule(), new DutyProbeModule()]);
        return services.BuildServiceProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class AuthorizationCollection : ICollectionFixture<AuthorizationFixture>
{
    public const string Name = "authorization";
}

public sealed class FixedPrincipal(Guid id, Guid? actingFor = null) : ICurrentPrincipal
{
    public Guid Id => id;

    public Guid? ActingForId => actingFor;
}

/// <summary>
/// Three duties for the delegation tests, as the slices that build approval, receiving and evaluation will declare
/// theirs (ADR-0037): each covers one of Authorization's read permissions.
/// </summary>
public sealed class DutyProbeModule : IModule
{
    public static readonly Duty Approval = new("probe.approval", "approval", "Approving for the probe.", [AuthorizationPermissions.UnitsRead]);

    public static readonly Duty Receiving = new("probe.receiving", "receiving", "Receiving for the probe.", [AuthorizationPermissions.RolesRead]);

    public static readonly Duty Evaluation = new("probe.evaluation", "evaluation", "Evaluating for the probe.", [AuthorizationPermissions.AssignmentsRead]);

    public string Name => "probe";

    public IReadOnlyList<Permission> Permissions => [];

    public IReadOnlyList<Duty> Duties => [Approval, Receiving, Evaluation];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
    }
}

/// <summary>A test principal taken from a request header; Identity replaces this with the session principal in T10.</summary>
public sealed class HeaderPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
{
    public const string HeaderName = "X-Test-Principal";

    public Guid Id => Guid.TryParse(accessor.HttpContext?.Request.Headers[HeaderName].ToString(), out var id) ? id : Guid.Empty;
}

/// <summary>A provider and one scope of it, disposed together.</summary>
public sealed class TestScope : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public TestScope(ServiceProvider provider, RecordingAuditWriter audit)
    {
        _provider = provider;
        Audit = audit;
        Scope = provider.CreateAsyncScope();
    }

    public AsyncServiceScope Scope { get; }

    public RecordingAuditWriter Audit { get; }

    public T Get<T>()
        where T : notnull => Scope.ServiceProvider.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        await Scope.DisposeAsync();
        await _provider.DisposeAsync();
    }
}

/// <summary>Stands in for Identity's principal directory (ADR-0031).</summary>
public sealed class TestPrincipals : IPrincipals
{
    private readonly ConcurrentDictionary<Guid, PrincipalSummary> _principals = new();

    public Guid Add(PrincipalKind kind = PrincipalKind.User, Guid? homeUnit = null)
    {
        var id = Ids.New();
        _principals[id] = new PrincipalSummary(id, kind, $"Principal {id:N}"[..18], homeUnit);
        return id;
    }

    public Task<IReadOnlyDictionary<Guid, PrincipalSummary>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        IReadOnlyDictionary<Guid, PrincipalSummary> found = ids.Where(_principals.ContainsKey).Distinct().ToDictionary(id => id, id => _principals[id]);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<Guid>> HomedAtAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. _principals.Values.Where(p => p.HomeUnitId is { } unit && unitIds.Contains(unit)).Select(p => p.Id)]);
}

public sealed class RecordingAuditWriter : IAuditWriter
{
    public List<AuditEvent> Events { get; } = [];

    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }
}
