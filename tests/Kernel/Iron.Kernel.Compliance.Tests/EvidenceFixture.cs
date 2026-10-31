using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api.ControlPlane;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Events;
using Iron.Kernel.Files;
using Iron.Kernel.Files.Contracts;
using Iron.Kernel.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Compliance.Tests;

/// <summary>
/// A unit's database and blob container for the evidence pack tests, with Audit, Authorization, Settings, Files and
/// Compliance, and a control plane that answers the unit's client as the real one does: its latest anchor with its id,
/// and its verification records. Tests share it in one collection, one after another.
/// </summary>
public sealed class EvidenceFixture : IAsyncLifetime
{
    public const string Container = "evidence-tests";

    private TestPostgres? _postgres;
    private TestBlob? _blob;

    public string ConnectionString => (_postgres ?? throw new InvalidOperationException("The fixture is not initialised.")).ConnectionString;

    public string StorageConnectionString => (_blob ?? throw new InvalidOperationException("The fixture is not initialised.")).ConnectionString;

    public EvidencePeople People { get; } = new();

    public StandInControlPlane ControlPlane { get; } = new();

    public async ValueTask InitializeAsync()
    {
        _postgres = await TestPostgres.StartAsync("evidence", TestContext.Current.CancellationToken);
        _blob = await TestBlob.StartAsync(TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new EvidenceActor());
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IObjectStorage>().EnsureContainerAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_blob is not null)
        {
            await _blob.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    public ServiceProvider BuildServices(FakeTimeProvider time, EvidenceActor actor, IReadOnlyDictionary<string, string?>? settings = null, DateTimeOffset? fieldHistorySince = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
        services.AddLogging();
        services.AddIronDatabase(ConnectionString);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = StorageConnectionString,
            ["Storage:Container"] = Container,
            ["ControlPlane:Address"] = StandInControlPlane.Address,
            ["ControlPlane:ClientId"] = "unit-echo",
            ["ControlPlane:ClientSecret"] = "a unit client secret",
        }).AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build();
        services.AddModules(configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new SettingsModule(), new FilesModule(), new ComplianceModule()]);
        services.AddSingleton<IPrincipals>(People);
        services.Replace(ServiceDescriptor.Singleton<ICurrentPrincipal>(actor));
        services.Replace(ServiceDescriptor.Singleton(provider => new ControlPlaneClient(provider.GetRequiredService<ControlPlaneOptions>(), provider.GetRequiredService<TimeProvider>(), ControlPlane.Handler())));
        if (fieldHistorySince is { } since)
        {
            services.Replace(ServiceDescriptor.Scoped<IRecordHistory>(provider => new HistoryKeptSince(new RecordHistoryReader(provider.GetRequiredService<AuditDbContext>()), since)));
        }

        return services.BuildServiceProvider();
    }
}

/// <summary>
/// The trail of a tenant made before T7, whose field history began at a given time; everything else as the trail answers.
/// The Audit tests prove when a real trail says it began.
/// </summary>
public sealed class HistoryKeptSince(RecordHistoryReader trail, DateTimeOffset since) : IRecordHistory
{
    public Task<IReadOnlyList<RecordHistoryEvent>> BetweenAsync(string recordType, DateTimeOffset from, DateTimeOffset? to, CancellationToken cancellationToken) =>
        trail.BetweenAsync(recordType, from, to, cancellationToken);

    public Task<DateTimeOffset?> OnlineSinceAsync(CancellationToken cancellationToken) => trail.OnlineSinceAsync(cancellationToken);

    public Task<DateTimeOffset?> KeptSinceAsync(CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(since);
}

[CollectionDefinition(Name)]
public sealed class EvidenceCollection : ICollectionFixture<EvidenceFixture>
{
    public const string Name = "evidence";
}

/// <summary>Who acts now, switched by the test: the requester, someone else, or the system as the worker is.</summary>
public sealed class EvidenceActor : ICurrentPrincipal
{
    public Guid Id { get; set; } = SystemPrincipal.SystemId;
}

/// <summary>The people Identity would answer for, by name.</summary>
public sealed class EvidencePeople : IPrincipals
{
    private readonly ConcurrentDictionary<Guid, PrincipalSummary> _people = new();

    public Guid Add(string name, PrincipalKind kind = PrincipalKind.User)
    {
        var id = Ids.New();
        _people[id] = new PrincipalSummary(id, kind, name, null, PrincipalStatuses.Active);
        return id;
    }

    public Task<IReadOnlyDictionary<Guid, PrincipalSummary>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, PrincipalSummary> found = ids.Where(_people.ContainsKey).Distinct().ToDictionary(id => id, id => _people[id]);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<Guid>> HomedAtAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);
}

/// <summary>
/// The control plane as the unit's client sees it (ADR-0050): a token for the client, the latest seal anchor with its id,
/// and the verification records of a period. The test sets what it answers.
/// </summary>
public sealed class StandInControlPlane
{
    public const string Address = "http://control-plane.test";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The latest seal anchor: position, hash and id.</summary>
    public (long Position, string Hash, Guid Id, DateTimeOffset At)? Seal { get; set; }

    public List<object> Verifications { get; } = [];

    /// <summary>A handler for one client: the client disposes it with its provider, and the answers stay here.</summary>
    public HttpMessageHandler Handler() => new Answering(this);

    private Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.RequestUri!.AbsolutePath;
        object? body = path switch
        {
            "/connect/token" => new Dictionary<string, object> { ["access_token"] = "a-token", ["expires_in"] = 3600 },
            "/api/v1/audit-anchors/latest" => new
            {
                Seal = Seal is { } seal ? new { Kind = "seal", seal.Position, seal.Hash, seal.At, ReceivedAt = seal.At.AddSeconds(1), Segment = 1, seal.Id } : null,
                Pruned = (object?)null,
                Segment = 1,
                Restores = Array.Empty<object>(),
            },
            "/api/v1/audit-verifications" => new { Verifications },
            _ => null,
        };
        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: Json) });
    }

    private sealed class Answering(StandInControlPlane plane) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => plane.AnswerAsync(request);
    }
}
