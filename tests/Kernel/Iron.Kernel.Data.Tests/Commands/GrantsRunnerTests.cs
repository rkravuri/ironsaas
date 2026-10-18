using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Tests.Sample;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Kernel.Data.Tests.Commands;

/// <summary>The app role may read and insert everywhere, change ordinary rows, and never change or delete append-only rows (TF-060 AC2).</summary>
[Collection(PostgresCollection.Name)]
public class GrantsRunnerTests(PostgresFixture fixture)
{
    private const string Role = "sample_app";
    private const string Password = "sample";

    [Fact]
    public void StatementsGiveReadWriteOnOrdinaryTablesAndReadInsertOnAppendOnlyTables()
    {
        using var context = new SampleDbContext(DesignTime.OptionsFor<SampleDbContext>());

        var statements = GrantsRunner.Statements("sample_db", Role, [("kernel_sample", context.Model)]);

        Assert.Contains("GRANT CONNECT ON DATABASE \"sample_db\" TO \"sample_app\"", statements);
        Assert.Contains("GRANT USAGE ON SCHEMA \"kernel_sample\" TO \"sample_app\"", statements);
        Assert.Contains("REVOKE ALL ON ALL TABLES IN SCHEMA \"kernel_sample\" FROM \"sample_app\"", statements);
        Assert.Contains("GRANT SELECT, INSERT, UPDATE, DELETE ON \"kernel_sample\".\"widgets\" TO \"sample_app\"", statements);
        Assert.Contains("GRANT SELECT, INSERT ON \"kernel_sample\".\"widget_events\" TO \"sample_app\"", statements);
        Assert.DoesNotContain(statements, s => s.Contains("UPDATE, DELETE ON \"kernel_sample\".\"widget_events\"", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesARoleNameThatIsNotAnIdentifier()
    {
        Assert.False(GrantsRunner.IsValidRole("acme-app"));
        Assert.False(GrantsRunner.IsValidRole("Acme"));
        Assert.False(GrantsRunner.IsValidRole("app; drop table"));
        Assert.True(GrantsRunner.IsValidRole("acme_app"));
    }

    [Fact]
    public async Task TheAppRoleCannotChangeOrDeleteAppendOnlyRows()
    {
        await CreateRoleAsync();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<GrantsRunner>().RunAsync(Role, TestContext.Current.CancellationToken);
        }

        await using var appDataSource = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Username = Role, Password = Password }.ToString()).Build();
        await using var connection = await appDataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => Run(connection, "update kernel_sample.widget_events set created_by = created_by"))).SqlState);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => Run(connection, "delete from kernel_sample.widget_events"))).SqlState);
        await Run(connection, "select count(*) from kernel_sample.widget_events");
        await Run(connection, "update kernel_sample.widgets set name = name where false");
    }

    private async Task CreateRoleAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await Run(connection, $"do $$ begin if not exists (select 1 from pg_roles where rolname = '{Role}') then create role {Role} login password '{Password}'; end if; end $$");
    }

    private static async Task Run(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
