using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Data.Tests.Commands;

[Collection(PostgresCollection.Name)]
public class DatabaseCommandsTests(PostgresFixture fixture)
{
    [Fact]
    [Proves("ADR-0011")]
    public void RecognisesItsCommandsAndStripsThemForTheHostBuilder()
    {
        Assert.True(DatabaseCommands.IsCommand(["migrate"]));
        Assert.True(DatabaseCommands.IsCommand(["seed"]));
        Assert.True(DatabaseCommands.IsCommand(["schema-check", "--verbose"]));
        Assert.False(DatabaseCommands.IsCommand([]));
        Assert.False(DatabaseCommands.IsCommand(["--urls", "http://localhost:5100"]));
        Assert.Equal(["--verbose"], DatabaseCommands.StripCommand(["schema-check", "--verbose"]));
        Assert.Equal(["--urls"], DatabaseCommands.StripCommand(["--urls"]));
    }

    [Fact]
    [Proves("ADR-0011", "ADR-0052")]
    public void TheFleetsPublishCommandKeepsItsVersionAndFileFromTheHostBuilder()
    {
        string[] publish = ["publish-validation-pack", "development", "/packs/validation-pack-development.zip"];

        Assert.True(DatabaseCommands.IsCommand(publish));
        Assert.Empty(DatabaseCommands.StripCommand(publish));
    }

    [Fact]
    [Proves("ADR-0011")]
    public async Task MigrateRunsOverEveryRegisteredContextAndOwnsAHistoryTablePerSchema()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));

        var exitCode = await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        await using var connection = await fixture.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from information_schema.tables where table_schema = 'kernel_sample' and table_name = '__ef_migrations_history'";
        Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0011")]
    public async Task SchemaCheckPassesForTheSampleSchema()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));

        var exitCode = await DatabaseCommands.RunAsync(provider, ["schema-check"], TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    [Proves("ADR-0011")]
    public async Task SeedRunsEverySeederLowestOrderFirst()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<ISeeder>(new RecordingSeeder("roles", 200, log));
        services.AddSingleton<ISeeder>(new RecordingSeeder("codes", 100, log));
        services.AddSingleton<ISeeder>(new RecordingSeeder("admin", 300, log));
        await using var provider = services.BuildServiceProvider();

        var runner = new SeedRunner(provider.GetServices<ISeeder>(), NullLogger<SeedRunner>.Instance);
        var ran = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["codes", "roles", "admin"], log);
        Assert.Equal(3, ran.Count);
    }

    [Fact]
    [Proves("ADR-0011")]
    public async Task SeedWithoutSeedersSucceedsAndDoesNothing()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));

        var exitCode = await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
    }

    private sealed class RecordingSeeder(string name, int order, List<string> log) : ISeeder
    {
        public int Order => order;

        public Task SeedAsync(CancellationToken cancellationToken)
        {
            log.Add(name);
            return Task.CompletedTask;
        }
    }
}
