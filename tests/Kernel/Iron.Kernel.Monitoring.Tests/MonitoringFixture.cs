using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Iron.Kernel.Api.ControlPlane;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Events;
using Iron.Kernel.History;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Monitoring.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Monitoring.Tests;

/// <summary>A PostgreSQL server for the module's tests; each test takes a tenant of its own, so rules and alerts never meet.</summary>
public sealed class MonitoringFixture : IAsyncLifetime
{
    private TestPostgres? _postgres;

    public async ValueTask InitializeAsync() => _postgres = await TestPostgres.StartAsync("monitoring", TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>A tenant of its own: a database migrated and seeded as the unit's commands do it.</summary>
    public async Task<MonitoredTenant> NewTenantAsync(MonitoringScope scope = MonitoringScope.Unit)
    {
        var name = $"monitoring{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var connectionString = await (_postgres ?? throw new InvalidOperationException("The fixture is not initialised.")).CreateDatabaseAsync(name, TestContext.Current.CancellationToken);
        var tenant = new MonitoredTenant(connectionString, scope);
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
        return tenant;
    }
}

[CollectionDefinition(Name)]
public sealed class MonitoringCollection : ICollectionFixture<MonitoringFixture>
{
    public const string Name = "monitoring";
}

/// <summary>A test tenant: its database, the operator's configuration of the rules, and the control plane as its unit sees it.</summary>
public sealed class MonitoredTenant(string connectionString, MonitoringScope scope)
{
    public string ConnectionString { get; } = connectionString;

    public MonitoringScope Scope { get; } = scope;

    /// <summary>The operator's values, as the unit secret gives them (<c>Monitoring:Rules:*</c>).</summary>
    public Dictionary<string, string?> Configuration { get; } = new(StringComparer.Ordinal);

    public ControlPlaneStandIn ControlPlane { get; } = new();

    public ServiceProvider Build(FakeTimeProvider time, Guid? principal = null, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<ICurrentPrincipal>(new FixedPrincipal(principal ?? SystemPrincipal.SystemId));
        services.AddSingleton(new TenantContext(TenantId.Parse(Scope == MonitoringScope.Unit ? "acme" : "control"), ["acme.localhost"]));
        var options = new ControlPlaneOptions { Address = "http://control.test", ClientId = "unit-acme", ClientSecret = "a unit client's secret" };
        services.AddSingleton(options);
        services.AddSingleton(new ControlPlaneClient(options, time, ControlPlane));
        services.AddIronDatabase(ConnectionString);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Configuration).Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddModules(configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new MonitoringModule(Scope), new DeclaredTypesModule()]);
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>Writes an event as a module would, at the clock's time.</summary>
    public async Task WriteAsync(FakeTimeProvider time, DateTimeOffset at, AuditEvent auditEvent)
    {
        time.SetUtcNow(at);
        await using var provider = Build(time);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(auditEvent, TestContext.Current.CancellationToken);
    }

    /// <summary>One pass of the rules at the clock's time.</summary>
    public async Task<EvaluationPass> EvaluateAsync(FakeTimeProvider time, DateTimeOffset at, Action<IServiceCollection>? more = null)
    {
        time.SetUtcNow(at);
        await using var provider = Build(time, more: more);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RuleEvaluator>().RunAsync(TestContext.Current.CancellationToken);
    }
}

public sealed class FixedPrincipal(Guid id) : ICurrentPrincipal
{
    public Guid Id => id;
}

/// <summary>
/// The event types the rules count that other modules declare (Identity, Keys, Settings, Protection, Tenancy), so a test
/// host without them runs every rule; the tests of a missing type leave it out.
/// </summary>
public sealed class DeclaredTypesModule(IReadOnlyList<string>? leaveOut = null) : IModule
{
    public string Name => "declared";

    public IReadOnlyList<Permission> Permissions => [];

    public IReadOnlyList<AuditEventType> AuditEventTypes =>
    [
        .. AlertRules.All.SelectMany(rule => rule.Matches).Select(m => m.EventType).Distinct(StringComparer.Ordinal)
            .Where(type => !type.StartsWith("audit.", StringComparison.Ordinal) && !type.StartsWith("authz.", StringComparison.Ordinal) && !type.StartsWith("monitoring.", StringComparison.Ordinal) && !type.StartsWith("events.", StringComparison.Ordinal))
            .Where(type => leaveOut?.Contains(type, StringComparer.Ordinal) != true)
            .Select(type => new AuditEventType(type, type)),
    ];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

/// <summary>The control plane as a unit's client meets it: a token, and every copy it is sent, recorded.</summary>
public sealed class ControlPlaneStandIn : HttpMessageHandler
{
    public ConcurrentQueue<(string Method, string Path, string Body)> Received { get; } = new();

    /// <summary>Set to refuse every copy, as a control plane that is down does.</summary>
    public HttpStatusCode CopyAnswer { get; set; } = HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        if (path == "/connect/token")
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"a token","expires_in":900}""", Encoding.UTF8, "application/json") };
        }

        Received.Enqueue((request.Method.Method, path, body));
        return new HttpResponseMessage(CopyAnswer) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
    }

    protected override void Dispose(bool disposing)
    {
    }
}
