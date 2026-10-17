using Iron.Data.Checks;
using Iron.Data.Tests.Sample;
using Microsoft.EntityFrameworkCore;

namespace Iron.Data.Tests;

[Collection(PostgresCollection.Name)]
public class ConventionsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TablesMadeFromTheConventionsPassTheSchemaCheck()
    {
        var violations = await SchemaCheck.RunAsync(fixture.DataSource, ["sample"], TestContext.Current.CancellationToken);

        Assert.Empty(violations);
    }

    [Fact]
    public async Task TheConventionsProduceSnakeCaseTablesWithTheStandardColumns()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            """
            select table_name::text || '.' || column_name::text
            from information_schema.columns
            where table_schema = 'sample'
            order by table_name, column_name
            """,
            connection);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        Assert.Contains("widgets.owning_organisation_id", columns);
        Assert.Contains("widgets.sharing_mode", columns);
        Assert.Contains("widgets.is_deleted", columns);
        Assert.Contains("widgets.version", columns);
        Assert.Contains("preferences.updated_by", columns);
        Assert.Contains("widget_events.created_at", columns);
        Assert.DoesNotContain("widget_events.updated_at", columns);
        Assert.DoesNotContain(columns, c => c.Any(char.IsUpper));
    }

    [Fact]
    public void TheModelPassesTheModelCheckForAPlatformModule()
    {
        var options = new DbContextOptionsBuilder<SampleDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        using var context = new SampleDbContext(options);

        Assert.Empty(ModelCheck.Check(context.Model, nameof(SampleDbContext), isBusinessModule: false));
    }

    [Fact]
    public void TheModelCheckFlagsABusinessEntityWithoutAnOwningOrganisation()
    {
        var options = new DbContextOptionsBuilder<SampleDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        using var context = new SampleDbContext(options);

        var violations = ModelCheck.Check(context.Model, nameof(SampleDbContext), isBusinessModule: true);

        // Preference is an AuditedEntity, not a BusinessRecord; WidgetEvent is append-only and also not a BusinessRecord.
        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v.Contains(nameof(Preference), StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains(nameof(WidgetEvent), StringComparison.Ordinal));
    }
}
