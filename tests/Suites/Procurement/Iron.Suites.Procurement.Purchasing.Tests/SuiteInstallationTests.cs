using Iron.Kernel;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Events;
using Iron.Suites.Procurement.Purchasing.Contracts;
using Iron.Suites.Procurement.Purchasing.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Suites.Procurement.Purchasing.Tests;

/// <summary>The run's PostgreSQL server; each test makes databases of its own, a unit's each.</summary>
public sealed class PurchasingFixture : IAsyncLifetime
{
    private TestPostgres? _postgres;

    public TestPostgres Postgres => _postgres ?? throw new InvalidOperationException("The fixture is not initialised.");

    public async ValueTask InitializeAsync() => _postgres = await TestPostgres.StartAsync("purchasing", TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PurchasingCollection : ICollectionFixture<PurchasingFixture>
{
    public const string Name = "purchasing";
}

/// <summary>
/// A suite's schema exists only where the unit runs the suite, its seed records the installation, and a unit that stops
/// running the suite keeps its schema and rows, which the suite finds again when it is added back (TF-244, ADR-0059).
/// </summary>
[Collection(PurchasingCollection.Name)]
public class SuiteInstallationTests(PurchasingFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    [Proves("TF-244 AC1", "TF-244 AC2", "ADR-0059")]
    public async Task OnlyAUnitThatRunsTheSuiteMigratesItsSchema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var with = await fixture.Postgres.CreateDatabaseAsync($"with_{Suffix()}", cancellationToken);
        var without = await fixture.Postgres.CreateDatabaseAsync($"without_{Suffix()}", cancellationToken);

        await RunAsync(with, withSuite: true, new FakeTimeProvider(Start), [], "migrate");
        await RunAsync(without, withSuite: false, new FakeTimeProvider(Start), [], "migrate");

        Assert.Equal(["installations"], await TablesAsync(with, "procurement_purchasing", cancellationToken));
        Assert.Empty(await TablesAsync(without, "procurement_purchasing", cancellationToken));
        Assert.NotEmpty(await TablesAsync(without, "kernel_audit", cancellationToken));
    }

    [Fact]
    [Proves("TF-244 AC1", "TF-244 AC3", "ADR-0059")]
    public async Task TheSeedRecordsTheInstallationWithItsJobAndReleaseAndAuditsIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await fixture.Postgres.CreateDatabaseAsync($"seeded_{Suffix()}", cancellationToken);
        var job = Ids.New();

        await RunAsync(database, withSuite: true, new FakeTimeProvider(Start), Settings(job, "2026.10.30.7"), "migrate", "seed");

        var installation = await InstallationAsync(database, cancellationToken);
        Assert.Equal(ProcurementSuite.ShortName, installation.Suite);
        Assert.Equal(Start, installation.InstalledAt);
        Assert.Equal(job, installation.InstalledByJob);
        Assert.Equal("2026.10.30.7", installation.InstalledVersion);
        Assert.Equal(job, installation.SeededByJob);
        Assert.Equal(1, await EventsAsync(database, PurchasingAuditEvents.SuiteInstalled, installation.Id, cancellationToken));
        Assert.Equal(0, await EventsAsync(database, PurchasingAuditEvents.SuiteSeeded, installation.Id, cancellationToken));
    }

    [Fact]
    [Proves("TF-244 AC4", "ADR-0059")]
    public async Task ARemovedSuiteKeepsItsSchemaAndRowsAndAddingItAgainFindsThem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await fixture.Postgres.CreateDatabaseAsync($"removed_{Suffix()}", cancellationToken);
        var first = Ids.New();
        var again = Ids.New();

        await RunAsync(database, withSuite: true, new FakeTimeProvider(Start), Settings(first, "2026.10.30.7"), "migrate", "seed");
        var installed = await InstallationAsync(database, cancellationToken);

        // Removed: the update runs the unit's migrate and seed without the suite; nothing of the suite runs, nothing goes.
        await RunAsync(database, withSuite: false, new FakeTimeProvider(Start.AddDays(1)), Settings(Ids.New(), "2026.10.31.1"), "migrate", "seed");
        Assert.Equal(["installations"], await TablesAsync(database, "procurement_purchasing", cancellationToken));
        var kept = await InstallationAsync(database, cancellationToken);
        Assert.Equal(installed.Id, kept.Id);
        Assert.Equal(installed.SeededByJob, kept.SeededByJob);

        // Added again: the same row, its first installation kept, the latest seed recorded beside it.
        await RunAsync(database, withSuite: true, new FakeTimeProvider(Start.AddDays(2)), Settings(again, "2026.11.01.3"), "migrate", "seed");
        var found = await InstallationAsync(database, cancellationToken);
        Assert.Equal(installed.Id, found.Id);
        Assert.Equal(Start, found.InstalledAt);
        Assert.Equal(first, found.InstalledByJob);
        Assert.Equal("2026.10.30.7", found.InstalledVersion);
        Assert.Equal(again, found.SeededByJob);
        Assert.Equal("2026.11.01.3", found.SeededVersion);
        Assert.Equal(Start.AddDays(2), found.SeededAt);
        Assert.Equal(1, await EventsAsync(database, PurchasingAuditEvents.SuiteInstalled, installed.Id, cancellationToken));
        Assert.Equal(1, await EventsAsync(database, PurchasingAuditEvents.SuiteSeeded, installed.Id, cancellationToken));
    }

    [Fact]
    [Proves("TF-244 AC2", "ADR-0059")]
    public void AUnitWhoseConfigurationNamesASuiteItsImageDoesNotCarryStopsNamingIt()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [InstalledSuites.ConfigurationKey] = "procurement,school" }).Build();

        var refused = Assert.Throws<InvalidOperationException>(() => InstalledSuites.From(configuration, [PurchasingModule.Suite()]));

        Assert.Contains("school", refused.Message, StringComparison.Ordinal);
        Assert.Empty(InstalledSuites.From(new ConfigurationBuilder().Build(), [PurchasingModule.Suite()]));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddModules(new ConfigurationBuilder().Build(), [new PurchasingModule()]));
    }

    [Fact]
    [Proves("ADR-0060", "ADR-0059")]
    public async Task ASuitesEventsAreDeclaredAndWrittenOnlyWhereTheUnitRunsTheSuite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var with = await fixture.Postgres.CreateDatabaseAsync($"events_with_{Suffix()}", cancellationToken);
        var without = await fixture.Postgres.CreateDatabaseAsync($"events_without_{Suffix()}", cancellationToken);

        await RunAsync(with, withSuite: true, new FakeTimeProvider(Start), Settings(Ids.New(), "2026.10.31.2"), "migrate", "seed");
        await RunAsync(without, withSuite: false, new FakeTimeProvider(Start), Settings(Ids.New(), "2026.10.31.2"), "migrate", "seed");

        await using (var unit = Unit(with, withSuite: true, new FakeTimeProvider(Start), []))
        {
            var declarations = unit.GetRequiredService<EventDeclarations>();
            Assert.NotNull(declarations.Find(PurchasingEvents.SuiteInstalled.Name, PurchasingEvents.SuiteInstalled.Version));
        }

        await using (var unit = Unit(without, withSuite: false, new FakeTimeProvider(Start), []))
        {
            var declarations = unit.GetRequiredService<EventDeclarations>();
            Assert.Null(declarations.Find(PurchasingEvents.SuiteInstalled.Name, PurchasingEvents.SuiteInstalled.Version));
            Assert.DoesNotContain(declarations.Events, e => e.Name.StartsWith("purchasing.", StringComparison.Ordinal));
        }

        var installation = await InstallationAsync(with, cancellationToken);
        Assert.Equal(1, await OutboxAsync(with, PurchasingEvents.SuiteInstalled.Name, installation.Id, cancellationToken));
        Assert.Equal(0, await OutboxAsync(without, PurchasingEvents.SuiteInstalled.Name, null, cancellationToken));
    }

    private static Dictionary<string, string?> Settings(Guid job, string version) => new()
    {
        ["Seed:JobId"] = job.ToString("D"),
        ["Release:Version"] = version,
    };

    /// <summary>A unit's migrate and seed, as the image's commands run them: the Foundation (here Audit and Events) and, when asked, the suite.</summary>
    private static async Task RunAsync(string connectionString, bool withSuite, TimeProvider time, Dictionary<string, string?> settings, params string[] commands)
    {
        await using var unit = Unit(connectionString, withSuite, time, settings);
        foreach (var command in commands)
        {
            Assert.Equal(0, await DatabaseCommands.RunAsync(unit, [command], TestContext.Current.CancellationToken));
        }
    }

    private static ServiceProvider Unit(string connectionString, bool withSuite, TimeProvider time, Dictionary<string, string?> settings)
    {
        settings[InstalledSuites.ConfigurationKey] = withSuite ? ProcurementSuite.ShortName : null;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var suites = InstalledSuites.From(configuration, [PurchasingModule.Suite()]);
        var services = new ServiceCollection();
        services.AddSingleton(time);
        services.AddSingleton<ICurrentPrincipal>(SystemPrincipal.Instance);
        services.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddIronDatabase(connectionString);
        services.AddModules(configuration, InstalledSuites.Compose([new AuditModule(), new EventsModule()], suites), suites);
        return services.BuildServiceProvider();
    }

    private static async Task<long> OutboxAsync(string connectionString, string type, Guid? recordId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select count(*) from kernel_events.outbox where type = @type and (@record::uuid is null or record_id = @record)", connection);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.Add(new NpgsqlParameter("record", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)recordId ?? DBNull.Value });
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<SuiteInstallation> InstallationAsync(string connectionString, CancellationToken cancellationToken)
    {
        // The provider options a host gives the context (snake_case names), on a connection of the test's own.
        await using var connection = new NpgsqlConnection(connectionString);
        var options = new DbContextOptionsBuilder<PurchasingDbContext>();
        DesignTime.Configure<PurchasingDbContext>(options, connection);
        await using var context = new PurchasingDbContext(options.Options);
        return await context.Installations.AsNoTracking().SingleAsync(cancellationToken);
    }

    private static async Task<List<string>> TablesAsync(string connectionString, string schema, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select table_name from information_schema.tables where table_schema = @schema and table_name not like '\\_\\_%' order by table_name", connection);
        command.Parameters.AddWithValue("schema", schema);
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task<long> EventsAsync(string connectionString, string eventType, Guid recordId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select count(*) from kernel_audit.events where event_type = @type and record_id = @record", connection);
        command.Parameters.AddWithValue("type", eventType);
        command.Parameters.AddWithValue("record", recordId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static string Suffix() => Ids.New().ToString("N")[^10..];
}
