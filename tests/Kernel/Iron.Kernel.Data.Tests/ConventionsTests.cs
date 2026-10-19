using Iron.Kernel.Data.Checks;
using Iron.Kernel.Data.Tests.Sample;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Data.Tests;

[Collection(PostgresCollection.Name)]
public class ConventionsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TablesMadeFromTheConventionsPassTheSchemaCheck()
    {
        var violations = await SchemaCheck.RunAsync(fixture.DataSource, ["kernel_sample"], TestContext.Current.CancellationToken);

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
            where table_schema = 'kernel_sample'
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
        Assert.Contains("proposals.status", columns);
        Assert.Contains("proposals.created_by", columns);
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

        // Preference is marked ITenantWide on purpose; WidgetEvent is append-only and neither a BusinessRecord nor marked.
        Assert.Equal(nameof(WidgetEvent), Assert.Single(violations).Split(' ')[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheModelCheckRefusesABusinessRecordCarryingEitherMarkerInEveryModule(bool isBusinessModule)
    {
        var options = new DbContextOptionsBuilder<ContextWithAMarkedRecord>().UseNpgsql(fixture.ConnectionString).Options;
        using var context = new ContextWithAMarkedRecord(options);

        var violations = ModelCheck.Check(context.Model, nameof(ContextWithAMarkedRecord), isBusinessModule);

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v.Contains("MarkedRecord is a BusinessRecord marked ITenantWide", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("OwnRuleRecord is a BusinessRecord marked IOwnAccessRule", StringComparison.Ordinal));
    }
}
