using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Api.ControlPlane;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Keys.Contracts;
using Iron.Kernel.Keys.Data;
using Iron.Kernel.Keys.Services;
using Iron.Kernel.Security.Keys;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Keys.Tests;

/// <summary>A tenant's database with the Audit, Authorization, Settings and Keys schemas, and OpenBao's transit engine.</summary>
public sealed class KeysFixture : IAsyncLifetime
{
    private TestPostgres? _postgres;
    private TestOpenBao? _store;

    public TestOpenBao Store => _store ?? throw new InvalidOperationException("The fixture is not initialised.");

    public async ValueTask InitializeAsync()
    {
        _postgres = await TestPostgres.StartAsync("keys", TestContext.Current.CancellationToken);
        _store = await TestOpenBao.StartAsync("keys", TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>A tenant of its own: a database migrated by the unit's command, and its three keys made as the fleet makes them.</summary>
    public async Task<Tenant> NewTenantAsync()
    {
        var name = $"keys{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var connectionString = await (_postgres ?? throw new InvalidOperationException("The fixture is not initialised.")).CreateDatabaseAsync(name, TestContext.Current.CancellationToken);
        var prefix = $"tenant-{name}";
        var keys = new TransitKeyService(new SecretStoreOptions { Address = Store.Address, Token = Store.RootToken, Mount = Store.Mount, TransitMount = Store.TransitMount }, new KeyOptions { Prefix = prefix });
        foreach (var purpose in KeyPurposes.All)
        {
            await keys.EnsureAsync(KeyPurposes.KeyName(prefix, purpose), KeyKinds.Of(purpose), TestContext.Current.CancellationToken);
        }

        var tenant = new Tenant(connectionString, prefix, keys, new ControlPlaneStandIn());
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        return tenant;
    }
}

[CollectionDefinition(Name)]
public sealed class KeysCollection : ICollectionFixture<KeysFixture>
{
    public const string Name = "keys";
}

/// <summary>A test tenant: its database, its key family, its key service and the control plane as its unit sees it.</summary>
public sealed class Tenant(string connectionString, string prefix, TransitKeyService keys, ControlPlaneStandIn controlPlane) : IDisposable
{
    public TransitKeyService Keys { get; } = keys;

    public ControlPlaneStandIn ControlPlane { get; } = controlPlane;

    public string KeyName(KeyPurpose purpose) => KeyPurposes.KeyName(prefix, purpose);

    public ServiceProvider Build(FakeTimeProvider time)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddLogging();
        services.AddSingleton<IKeyService>(Keys);
        var options = new ControlPlaneOptions { Address = "http://control.test", ClientId = "unit-test", ClientSecret = "a unit client's secret" };
        services.AddSingleton(options);
        services.AddSingleton(new ControlPlaneClient(options, time, ControlPlane));
        services.AddIronDatabase(connectionString);
        services.AddModules(new ConfigurationBuilder().Build(), [new AuditModule(), new AuthorizationModule(), new SettingsModule(), new KeysModule()]);
        return services.BuildServiceProvider();
    }

    public void Dispose() => Keys.Dispose();
}

/// <summary>
/// The control plane as a unit's client meets it: a token, the tenant's keys and rotations, and what the unit reports,
/// recorded. A rotation reported finished is finished here, as the real one records it.
/// </summary>
public sealed class ControlPlaneStandIn : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = ControlPlaneClient.Json;

    public List<Dictionary<string, object?>> Keys { get; } = [];

    public List<Dictionary<string, object?>> Rotations { get; } = [];

    public ConcurrentQueue<(string Method, string Path, string Body)> Received { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Received.Enqueue((request.Method.Method, path, body));
        object answer = path switch
        {
            "/connect/token" => new { access_token = "a token", expires_in = 900 },
            "/api/v1/key-rotations" => new { keys = Keys, rotations = Rotations },
            "/api/v1/key-rotations/policy" => new { data_days = JsonDocument.Parse(body).RootElement.GetProperty("data_days").GetInt32() },
            _ when path.EndsWith("/progress", StringComparison.Ordinal) => Progress(path, body),
            _ => new { },
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(answer, Json), Encoding.UTF8, "application/json") };
    }

    /// <summary>Each host the tests build disposes its client; the stand-in lives for the tenant.</summary>
    protected override void Dispose(bool disposing)
    {
    }

    private object Progress(string path, string body)
    {
        var id = path.Split('/')[^2];
        var report = JsonDocument.Parse(body).RootElement;
        var rotation = Rotations.Single(r => (string)r["id"]! == id);
        rotation["done"] = report.GetProperty("done").GetInt64();
        rotation["total"] = report.GetProperty("total").GetInt64();
        if (report.GetProperty("finished").GetBoolean())
        {
            rotation["state"] = "finished";
            rotation["finished_at"] = DateTimeOffset.UtcNow;
        }

        return rotation;
    }
}

/// <summary>
/// A unit's side of key rotation (TF-075, ADR-0054): a rotation recorded in the tenant's trail with who asked and why, the
/// values moved to the new version and the end reported, the retirement recorded, the policy told; and key checks found by
/// their exact value before and after (TF-074 AC2).
/// </summary>
[Collection(KeysCollection.Name)]
public class KeyRotationSyncTests(KeysFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-075 AC1", "TF-075 AC3", "TF-074 AC2", "ADR-0054")]
    public async Task ARotationIsRecordedWithWhoAskedAndWhyItsValuesMoveAndItsRetirementIsRecorded()
    {
        using var tenant = await fixture.NewTenantAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = tenant.Build(time);
        await StoreCheckAsync(provider, "First check", "AB-123-456-789");
        await StoreCheckAsync(provider, "Second check", "CD-987-654-321");

        // The fleet rotated the index key at a key officer's request; the control plane says so.
        await tenant.Keys.RotateAsync(tenant.KeyName(KeyPurpose.Index), Cancel);
        var rotationId = Ids.New();
        tenant.ControlPlane.Keys.AddRange([Key("data", 1, 1), Key("index", 2, 1), Key("signing", 1, 1), Key("database", 1, 1, 90)]);
        tenant.ControlPlane.Rotations.Add(Rotation(rotationId, "index", "rotated", 1, 2, "Olga Operator", "The quarterly key drill"));

        await RunAsync(provider);
        var entry = await EntryAsync(provider, rotationId);
        var reported = tenant.ControlPlane.Received.Where(r => r.Path.EndsWith("/progress", StringComparison.Ordinal)).Select(r => JsonDocument.Parse(r.Body).RootElement).Last();

        // The fleet retires the old version; the unit records it at its next pass.
        await tenant.Keys.RetireAsync(tenant.KeyName(KeyPurpose.Index), 2, Cancel);
        tenant.ControlPlane.Rotations[0]["state"] = "retired";
        tenant.ControlPlane.Rotations[0]["retired_at"] = DateTimeOffset.UtcNow;
        await RunAsync(provider);
        var retired = await EntryAsync(provider, rotationId);

        Assert.Equal((2L, 2L), (entry.Done, entry.Total));
        Assert.NotNull(entry.FinishedAt);
        Assert.Equal((2L, true), (reported.GetProperty("done").GetInt64(), reported.GetProperty("finished").GetBoolean()));
        Assert.NotNull(retired.RetiredAt);
        await using var scope = provider.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.RecordId == rotationId).OrderBy(e => e.CreatedAt).ToListAsync(Cancel);
        Assert.Equal([KeysAuditEvents.KeyRotated, KeysAuditEvents.ReencryptionFinished, KeysAuditEvents.KeyRetired], events.Select(e => e.EventType));
        Assert.All(events, e => Assert.Equal("The quarterly key drill", e.Reason));
        Assert.All(events, e => Assert.Equal("Olga Operator", JsonDocument.Parse(e.Details!).RootElement.GetProperty("requested_by").GetString()));
        var checks = scope.ServiceProvider.GetRequiredService<KeyChecks>();
        Assert.Equal(["AB-123-456-789"], (await checks.FindAsync("AB123456789", Cancel)).Select(m => m.Value));
        Assert.Empty(await checks.FindAsync("123-456", Cancel));
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<KeysDbContext>().States.CountAsync(s => s.Key == "index" && s.KeyVersion == 2, Cancel));
    }

    [Fact]
    [Proves("ADR-0054")]
    public async Task TheTenantsPolicyIsToldToTheControlPlaneWhenItDiffersFromWhatItKeeps()
    {
        using var tenant = await fixture.NewTenantAsync();
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        tenant.ControlPlane.Keys.AddRange([Key("data", 1, 1, 100), Key("index", 1, 1, 100)]);

        await RunAsync(provider);
        tenant.ControlPlane.Keys.Clear();
        tenant.ControlPlane.Keys.AddRange([Key("data", 1, 1, 365), Key("index", 1, 1, 365)]);
        await RunAsync(provider);

        var policies = tenant.ControlPlane.Received.Where(r => r.Method == "PUT").Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("data_days").GetInt32()).ToList();
        Assert.Equal([365], policies);
    }

    [Fact]
    [Proves("TF-074 AC1", "ADR-0054")]
    public async Task AKeyChecksValueIsStoredAsCiphertextAndItsHistorySaysOnlyThatItWasSet()
    {
        using var tenant = await fixture.NewTenantAsync();
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var id = await StoreCheckAsync(provider, "Stored check", "GB33BUKB20201555555555");

        await using var scope = provider.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<KeysDbContext>().Database.SqlQuery<string>($"SELECT value AS \"Value\" FROM kernel_keys.key_checks WHERE id = {id}").SingleAsync(Cancel);
        var history = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.RecordId == id && e.Changes != null).Select(e => e.Changes!).SingleAsync(Cancel);

        Assert.StartsWith("vault:v1:", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("20201555", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("20201555", history, StringComparison.Ordinal);
        Assert.DoesNotContain("vault:v1:", history, StringComparison.Ordinal);
    }

    private static async Task<Guid> StoreCheckAsync(ServiceProvider provider, string label, string value)
    {
        await using var scope = provider.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<KeyChecks>().StoreAsync(label, value, Cancel);
        Assert.True(stored.IsSuccess, stored.IsFailure ? stored.Error.Message : null);
        return stored.Value.Id;
    }

    private static async Task RunAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<KeyRotationSync>().RunOnceAsync(Cancel);
    }

    private static async Task<KeyRotationEntry> EntryAsync(ServiceProvider provider, Guid id)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<KeysDbContext>().Rotations.AsNoTracking().SingleAsync(r => r.Id == id, Cancel);
    }

    private static Dictionary<string, object?> Key(string key, int version, int minimum, int days = 365) => new()
    {
        ["key"] = key,
        ["version"] = version,
        ["minimum_version"] = minimum,
        ["rotated_at"] = null,
        ["next_rotation_at"] = DateTimeOffset.UtcNow.AddDays(days),
        ["rotation_days"] = days,
    };

    private static Dictionary<string, object?> Rotation(Guid id, string key, string state, int from, int to, string requestedBy, string reason) => new()
    {
        ["id"] = id.ToString("D"),
        ["key"] = key,
        ["trigger"] = "operator",
        ["requested_by"] = requestedBy,
        ["reason"] = reason,
        ["state"] = state,
        ["from_version"] = from,
        ["to_version"] = to,
        ["rotated_at"] = DateTimeOffset.UtcNow,
        ["finished_at"] = null,
        ["retired_at"] = null,
    };
}
