using Azure;
using Azure.Storage.Blobs;
using Iron.Host.ControlPlane;
using Iron.Kernel;
using Iron.Kernel.Api.Tenancy;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Fleet.Services;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iron.Host.Tests;

/// <summary>
/// The control plane's database as its image runs it (ADR-0053), on the run's PostgreSQL and Azurite: first as before
/// T9a, migrated by the superuser; then prepared by the fleet's command, migrated by the owner role, and run by the app
/// role, with its chain anchored in a container it may only add to and verified by the read-only role.
/// </summary>
public sealed class ControlPlaneDatabaseFixture : IAsyncLifetime
{
    private TestPostgres? _postgres;
    private TestBlob? _blob;

    public MemorySecrets Secrets { get; } = new();

    /// <summary>A superuser connection to the server's <c>postgres</c> database, as the fleet's admin connection is.</summary>
    public string Server { get; private set; } = string.Empty;

    public string Storage => (_blob ?? throw new InvalidOperationException("The fixture is not initialised.")).ConnectionString;

    public WebApplication Runtime { get; private set; } = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _postgres = await TestPostgres.StartAsync("controlplane", Cancel);
        _blob = await TestBlob.StartAsync(Cancel);
        Server = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { Database = "postgres" }.ConnectionString;
        await StartAfreshAsync();

        // Before T9a: the superuser made the database and migrated it.
        await ExecuteAsync(Server, "create database control");
        await using (var before = Build(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = Database("control", Server) }))
        {
            Assert.Equal(0, await DatabaseCommands.RunAsync(before.Services, [DatabaseCommands.Migrate], Cancel));
        }

        // make up-control: the base secret, the fleet's command, then migrate as the owner from the migrate secret.
        await Secrets.WriteAsync(ControlPlaneNames.UnitSecretPath, new Dictionary<string, string> { ["Telemetry:ExportToCollector"] = "false" }, Cancel);
        var options = new FleetOptions();
        options.Database.AdminConnectionString = Server;
        options.Storage.AccountConnectionString = Storage;
        Assert.Equal(0, await new ControlPlaneDatabase(options, Secrets, TimeProvider.System).RunAsync([], Cancel));
        await using (var owner = Build(await SecretAsync(ControlPlaneNames.MigrateSecretPath)))
        {
            Assert.Equal(0, await DatabaseCommands.RunAsync(owner.Services, [DatabaseCommands.Migrate], Cancel));
        }

        Runtime = Build(await SecretAsync(ControlPlaneNames.UnitSecretPath));
    }

    public async ValueTask DisposeAsync()
    {
        if (Runtime is not null)
        {
            await Runtime.DisposeAsync();
        }

        if (_blob is not null)
        {
            await _blob.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>The control plane's database as one of its roles reaches it.</summary>
    public async Task<string> AsAsync(string role)
    {
        var passwords = await Secrets.ReadAsync(ControlPlaneNames.DatabaseSecretPath, Cancel) ?? throw new InvalidOperationException("Not prepared.");
        var key = role switch
        {
            ControlPlaneNames.OwnerRole => ControlPlaneDatabase.OwnerPasswordKey,
            ControlPlaneNames.AppRole => ControlPlaneDatabase.AppPasswordKey,
            _ => ControlPlaneDatabase.ReaderPasswordKey,
        };
        return ControlPlaneDatabase.ConnectionString(Server, role, passwords[key]);
    }

    public static string Database(string name, string server) => new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    /// <summary>The control plane's services as its image composes them, on these settings and the in-memory store.</summary>
    private WebApplication Build(IReadOnlyDictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tenant:Id"] = "control",
            ["Tenant:Hostnames:0"] = "localhost",
            ["Telemetry:ExportToCollector"] = "false",
            ["Fleet:Database:AdminConnectionString"] = Server,
            ["Fleet:Storage:AccountConnectionString"] = Storage,
        });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddControlPlane(TenantBinding.FromConfiguration(builder.Configuration));
        builder.Services.Replace(ServiceDescriptor.Singleton<ISecretStore>(Secrets));
        return builder.Build();
    }

    private async Task<IReadOnlyDictionary<string, string?>> SecretAsync(string path) =>
        (await Secrets.ReadAsync(path, Cancel) ?? throw new InvalidOperationException($"No secret at {path}.")).ToDictionary(p => p.Key, p => (string?)p.Value);

    /// <summary>A server another run left a control database on, or a project run twice alone: the names are the server's.</summary>
    private async Task StartAfreshAsync()
    {
        await ExecuteAsync(Server, "drop database if exists control with (force)");
        foreach (var role in new[] { ControlPlaneNames.OwnerRole, ControlPlaneNames.AppRole, ControlPlaneNames.ReaderRole })
        {
            await ExecuteAsync(Server, $"drop role if exists {role}");
        }

        await new BlobContainerClient(Storage, ControlPlaneNames.AnchorsContainer).DeleteIfExistsAsync(cancellationToken: Cancel);
    }
}

public sealed class MemorySecrets : ISecretStore
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _values = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryGetValue(path, out var values) ? values : null);

    public Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        _values[path] = new Dictionary<string, string>(values, StringComparer.Ordinal);
        return Task.CompletedTask;
    }

    public Task<bool> IsReachableAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}

/// <summary>
/// The control plane's own role and chain (T6's later item, ADR-0053): it runs with a role that cannot change its
/// schema or its sealed events, its chain is anchored outside its database where it cannot change the anchors, and a
/// separate read-only role verifies it.
/// </summary>
public class ControlPlaneDatabaseTests(ControlPlaneDatabaseFixture fixture) : IClassFixture<ControlPlaneDatabaseFixture>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-060 AC2", "ADR-0053")]
    public async Task TheControlPlanesRoleCannotAlterItsSchemaOrItsSealedEventsAndOwnsNothing()
    {
        await WriteAndSealAsync(3);
        var app = await fixture.AsAsync(ControlPlaneNames.AppRole);
        var refused = new List<string>();
        foreach (var sql in new[]
        {
            "alter table kernel_audit.events add column probe int",
            "alter table kernel_audit.events disable trigger events_guard",
            "update kernel_audit.events set reason = 'changed'",
            "delete from kernel_audit.events",
            "truncate kernel_audit.events",
            "create table kernel_audit.probe (id int)",
            "drop table kernel_tenancy.audit_anchors",
            "create schema probe",
        })
        {
            try
            {
                await ControlPlaneDatabaseFixture.ExecuteAsync(app, sql);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                refused.Add(sql);
            }
        }

        Assert.Equal(8, refused.Count);

        // A database the superuser made before T9a moved to the owner, with everything in it.
        await using var owners = new NpgsqlConnection(ControlPlaneDatabaseFixture.Database(ControlPlaneNames.Database, fixture.Server));
        await owners.OpenAsync(Cancel);
        await using var count = new NpgsqlCommand(
            "select (select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname like 'kernel\\_%' and pg_get_userbyid(c.relowner) <> 'control_owner') " +
            "+ (select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname like 'kernel\\_%' and pg_get_userbyid(p.proowner) <> 'control_owner') " +
            "+ (select count(*) from pg_database where datname = 'control' and pg_get_userbyid(datdba) <> 'control_owner')",
            owners);
        Assert.Equal(0L, await count.ExecuteScalarAsync(Cancel));
    }

    [Fact]
    [Proves("TF-061 AC1", "TF-061 AC2", "ADR-0053")]
    public async Task ItsChainIsAnchoredWhereItCannotChangeTheAnchorsVerifiedByTheReadOnlyRoleAndATamperedEventIsFound()
    {
        var events = await WriteAndSealAsync(4);
        await using (var scope = fixture.Runtime.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuditAnchorSink>().SendAsync(Cancel);
        }

        // The control plane's own SAS may add an anchor, and neither write over one nor delete one.
        var sas = (await fixture.Secrets.ReadAsync(ControlPlaneNames.UnitSecretPath, Cancel))!["ControlAudit:AnchorsSasUrl"];
        var container = new BlobContainerClient(new Uri(sas));
        var anchors = new List<string>();
        await foreach (var blob in container.GetBlobsAsync(cancellationToken: Cancel))
        {
            anchors.Add(blob.Name);
        }

        Assert.NotEmpty(anchors);
        var anchor = container.GetBlobClient(anchors[^1]);
        Assert.Equal(403, (await Assert.ThrowsAsync<RequestFailedException>(() => anchor.DeleteAsync(cancellationToken: Cancel))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<RequestFailedException>(() => anchor.UploadAsync(BinaryData.FromString("{}"), overwrite: true, Cancel))).Status);

        // The read-only role reads the chain and nothing it may change.
        var reader = await fixture.AsAsync(ControlPlaneNames.ReaderRole);
        await Assert.ThrowsAsync<PostgresException>(() => ControlPlaneDatabaseFixture.ExecuteAsync(reader, "update kernel_audit.events set reason = 'changed'"));
        await Assert.ThrowsAsync<PostgresException>(() => ControlPlaneDatabaseFixture.ExecuteAsync(reader, "select count(*) from kernel_identity.principals"));

        await using var scopeOfFleet = fixture.Runtime.Services.CreateAsyncScope();
        var verifier = scopeOfFleet.ServiceProvider.GetRequiredService<ControlAuditVerifier>();
        var chains = scopeOfFleet.ServiceProvider.GetRequiredService<ITenantAuditChains>();
        var intact = await verifier.VerifyAsync(full: true, Cancel);
        var recorded = await chains.LatestVerificationAsync(ControlPlaneNames.Slug, false, Cancel);

        Assert.Equal(AuditVerificationResults.Passed, intact.Result);
        Assert.True(intact.EventsChecked >= events.Count);
        Assert.Equal((AuditVerificationResults.Passed, intact.ToPosition), (recorded!.Result, recorded.ToPosition));

        // Someone with the superuser's rights changes a sealed event behind the trigger's back.
        var tampered = events[1];
        await ControlPlaneDatabaseFixture.ExecuteAsync(
            ControlPlaneDatabaseFixture.Database(ControlPlaneNames.Database, fixture.Server),
            $"alter table kernel_audit.events disable trigger events_guard; update kernel_audit.events set target = 'changed' where id = '{tampered:D}'; alter table kernel_audit.events enable trigger events_guard");
        var broken = await verifier.VerifyAsync(full: true, Cancel);

        Assert.Equal(AuditVerificationResults.Failed, broken.Result);
        Assert.Contains(broken.Problems, problem => problem.EventId == tampered);
        await using var alerts = new NpgsqlConnection(ControlPlaneDatabaseFixture.Database(ControlPlaneNames.Database, fixture.Server));
        await alerts.OpenAsync(Cancel);
        await using var alert = new NpgsqlCommand("select count(*) from kernel_audit.events where event_type = @type and severity = 'high'", alerts);
        alert.Parameters.AddWithValue("type", TenancyAuditEvents.AuditChainBroken);
        Assert.True((long)(await alert.ExecuteScalarAsync(Cancel))! >= 1, "A chain that did not pass raises the alert, a high-severity event.");
    }

    /// <summary>Events the control plane writes as its app role, sealed into its chain.</summary>
    private async Task<IReadOnlyList<Guid>> WriteAndSealAsync(int count)
    {
        await using var scope = fixture.Runtime.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        var targets = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var target = $"control-test-{Ids.New():N}";
            targets.Add(target);
            await writer.WriteAsync(new AuditEvent("tenancy.tenant_registered", AuditOutcome.Success, SystemPrincipal.SystemId, target), Cancel);
        }

        var sealer = scope.ServiceProvider.GetRequiredService<AuditSealer>();
        while (await sealer.SealAsync(Cancel) is not null)
        {
        }

        await using var connection = new NpgsqlConnection(ControlPlaneDatabaseFixture.Database(ControlPlaneNames.Database, fixture.Server));
        await connection.OpenAsync(Cancel);
        await using var ids = new NpgsqlCommand("select id from kernel_audit.events where target = any(@targets) and chain_position is not null order by chain_position", connection);
        ids.Parameters.AddWithValue("targets", targets.ToArray());
        var found = new List<Guid>();
        await using var reader = await ids.ExecuteReaderAsync(Cancel);
        while (await reader.ReadAsync(Cancel))
        {
            found.Add(reader.GetGuid(0));
        }

        Assert.Equal(count, found.Count);
        return found;
    }
}
