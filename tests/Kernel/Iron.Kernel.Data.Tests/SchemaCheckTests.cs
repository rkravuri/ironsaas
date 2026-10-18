using Iron.Kernel.Data.Checks;
using Iron.Kernel.Data.Tests.Sample;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Iron.Kernel.Data.Tests;

public class SchemaCheckRuleTests
{
    private static readonly IReadOnlyList<ColumnInfo> AuditedColumns =
    [
        new("id", SchemaCheck.Uuid, false),
        new("created_at", SchemaCheck.Timestamp, false),
        new("created_by", SchemaCheck.Uuid, false),
        new("updated_at", SchemaCheck.Timestamp, false),
        new("updated_by", SchemaCheck.Uuid, false),
        new("version", SchemaCheck.Integer, false),
    ];

    [Fact]
    public void AWellFormedTableHasNoViolations()
    {
        var table = new TableInfo("identity", "principals", null, [.. AuditedColumns, new("login_id", "text", false)], ["id"]);

        Assert.Empty(SchemaCheck.Evaluate([table], []));
    }

    [Fact]
    public void AnAppendOnlyTableNeedsOnlyTheCreatedColumns()
    {
        var table = new TableInfo(
            "audit",
            "events",
            "append-only",
            [new("id", SchemaCheck.Uuid, false), new("created_at", SchemaCheck.Timestamp, false), new("created_by", SchemaCheck.Uuid, false)],
            ["id"]);

        Assert.Empty(SchemaCheck.Evaluate([table], []));
    }

    [Fact]
    public void MissingStandardColumnsAreNamed()
    {
        var table = new TableInfo("identity", "sessions", null, [new("id", SchemaCheck.Uuid, false)], ["id"]);

        var violations = SchemaCheck.Evaluate([table], []);

        Assert.Contains(violations, v => v.Contains("missing column 'created_at'", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("missing column 'version'", StringComparison.Ordinal));
    }

    [Fact]
    public void WrongTypesNullabilityAndKeysAreNamed()
    {
        var columns = new List<ColumnInfo>(AuditedColumns);
        columns[0] = new ColumnInfo("id", "bigint", false);
        columns[1] = new ColumnInfo("created_at", SchemaCheck.Timestamp, true);
        var table = new TableInfo("identity", "principals", null, columns, ["id", "login_id"]);

        var violations = SchemaCheck.Evaluate([table], []);

        Assert.Contains(violations, v => v.Contains("expected type uuid, found bigint", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("created_at: must be NOT NULL", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("single column 'id'", StringComparison.Ordinal));
    }

    [Fact]
    public void NamesThatAreNotSnakeCaseAreNamed()
    {
        var table = new TableInfo("identity", "Principals", null, [.. AuditedColumns, new("LoginId", "text", false)], ["id"]);

        var violations = SchemaCheck.Evaluate([table], []);

        Assert.Contains(violations, v => v.Contains("identity.Principals: table names are snake_case", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("identity.Principals.LoginId: column names are snake_case", StringComparison.Ordinal));
    }

    [Fact]
    public void SoftDeleteColumnsMustComeTogether()
    {
        var table = new TableInfo("identity", "principals", null, [.. AuditedColumns, new("is_deleted", SchemaCheck.Boolean, false)], ["id"]);

        var violations = SchemaCheck.Evaluate([table], []);

        Assert.Contains(violations, v => v.Contains("missing column 'deleted_at'", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("missing column 'deleted_by'", StringComparison.Ordinal));
    }

    [Fact]
    public void AForeignKeyIntoAnotherSchemaIsNamed()
    {
        var foreignKey = new ForeignKeyInfo("fk_orders_supplier", "procurement", "orders", "identity", "principals");

        var violation = Assert.Single(SchemaCheck.Evaluate([], [foreignKey]));

        Assert.Contains("procurement.orders: foreign key fk_orders_supplier points into schema identity", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMigrationsHistoryTableIsIgnored()
    {
        var table = new TableInfo("identity", "__ef_migrations_history", null, [new("migration_id", "text", false)], ["migration_id"]);

        Assert.Empty(SchemaCheck.Evaluate([table], []));
    }

    [Theory]
    [InlineData("principals", true)]
    [InlineData("login_id2", true)]
    [InlineData("Principals", false)]
    [InlineData("login-id", false)]
    [InlineData("_private", false)]
    [InlineData("", false)]
    public void SnakeCaseIsLowercaseLettersDigitsAndUnderscores(string name, bool expected)
    {
        Assert.Equal(expected, SchemaCheck.IsSnakeCase(name));
    }
}

[Collection(PostgresCollection.Name)]
public class SchemaCheckDatabaseTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ReportsRealTablesThatBreakTheConventions()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var setup = new NpgsqlCommand(
            """
            create schema if not exists bad;
            create table if not exists bad.notes (
                id uuid primary key,
                created_at timestamp with time zone not null,
                created_by uuid not null,
                "Title" text not null
            );
            create table if not exists bad.links (
                id uuid primary key,
                created_at timestamp with time zone not null,
                created_by uuid not null,
                updated_at timestamp with time zone not null,
                updated_by uuid not null,
                version integer not null,
                widget_id uuid not null references sample.widgets (id)
            );
            """,
            connection))
        {
            await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var violations = await SchemaCheck.RunAsync(connection, ["bad"], TestContext.Current.CancellationToken);

        Assert.Contains(violations, v => v.Contains("bad.notes: missing column 'updated_at'", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("bad.notes.Title: column names are snake_case", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("bad.links: foreign key", StringComparison.Ordinal) && v.Contains("points into schema sample", StringComparison.Ordinal));
    }

    [Fact]
    public void AContextWithoutASchemaAttributeFailsClearly()
    {
        var options = new DbContextOptionsBuilder<ContextWithoutSchema>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        var error = Assert.Throws<InvalidOperationException>(() => new ContextWithoutSchema(options));

        Assert.Contains("[Schema", error.Message, StringComparison.Ordinal);
    }
}
