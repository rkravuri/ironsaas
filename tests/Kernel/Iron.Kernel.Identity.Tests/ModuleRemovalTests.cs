using Iron.Kernel.Data.Checks;
using Npgsql;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// A module can be taken out because nothing outside its schema refers to it (TF-080, ADR-0008): four real modules
/// migrated and seeded in a database of the test's own, one schema dropped, and the others still pass the schema check
/// with every table and row in place.
/// </summary>
[Collection(IdentityCollection.Name)]
public class ModuleRemovalTests(IdentityFixture fixture)
{
    private static readonly string[] Remaining = ["kernel_audit", "kernel_authz", "kernel_identity"];

    [Fact]
    [Proves("TF-080 AC3", "ADR-0008")]
    public async Task DroppingOneModulesSchemaLeavesTheOtherSchemasValidWithTheirTablesAndRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.CreateTenantDatabaseAsync($"removal_{Ids.New():N}");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        Assert.Empty(await SchemaCheck.RunAsync(dataSource, [.. Remaining, "kernel_settings"], ct));
        var tablesBefore = await TablesAsync(dataSource, ct);
        var principalsBefore = await CountAsync(dataSource, "kernel_identity.principals", ct);
        var rolesBefore = await CountAsync(dataSource, "kernel_authz.roles", ct);

        // The module is removed: its schema goes, and with it whatever lives in it, but nothing else.
        await using (var drop = dataSource.CreateCommand("drop schema kernel_settings cascade"))
        {
            await drop.ExecuteNonQueryAsync(ct);
        }

        Assert.Empty(await SchemaCheck.RunAsync(dataSource, Remaining, ct));
        Assert.Equal(tablesBefore, await TablesAsync(dataSource, ct));
        Assert.True(principalsBefore > 0 && rolesBefore > 0, "the seed made the administrator and the system roles");
        Assert.Equal(principalsBefore, await CountAsync(dataSource, "kernel_identity.principals", ct));
        Assert.Equal(rolesBefore, await CountAsync(dataSource, "kernel_authz.roles", ct));
    }

    private static async Task<List<string>> TablesAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select table_schema || '.' || table_name from information_schema.tables where table_schema = any($1) order by 1");
        command.Parameters.Add(new NpgsqlParameter { Value = Remaining });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource, string table, CancellationToken cancellationToken)
    {
        // The table names are this test's own constants, never input.
        await using var command = dataSource.CreateCommand($"select count(*) from {table}");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
