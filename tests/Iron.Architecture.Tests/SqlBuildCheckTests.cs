using System.Globalization;
using System.Text.RegularExpressions;
using Iron.Kernel;

namespace Iron.Architecture.Tests;

/// <summary>
/// The build refuses SQL built from strings (TF-071 AC3, ADR-0053). A fixture Kernel project written next to this test's
/// assembly inherits the repository's build settings, as every project of the product does, so Directory.Build.props
/// puts the analyzer of tools/Iron.Analyzers into its compile; it uses the real EF Core and Npgsql.
/// </summary>
public partial class SqlBuildCheckTests
{
    private const string Probe = """
        using Iron.Kernel;
        using Microsoft.EntityFrameworkCore;
        using Npgsql;

        namespace Fixture;

        public sealed class Row
        {
            public int Id { get; set; }
        }

        public static class Probe
        {
            private const string Constant = "select 1";

            public static async Task RunAsync(DbContext context, NpgsqlConnection connection, NpgsqlDataSource source, string name, int id)
            {
                await using var made = source.CreateCommand("select * from t where name = '" + name + "'"); // refused: a data source's command
                await using var parameterised = source.CreateCommand("select * from t where name = @name"); // accepted: a parameter
                await context.Database.ExecuteSqlRawAsync("delete from t where name = '" + name + "'"); // refused: concatenation
                await context.Database.ExecuteSqlRawAsync($"delete from t where id = {id}"); // refused: interpolation into raw
                context.Database.ExecuteSqlRaw(string.Format("delete from t where name = '{0}'", name)); // refused: formatting
                _ = context.Set<Row>().FromSqlRaw("select * from t where name = '" + name + "'"); // refused: a query
                await using var built = new NpgsqlCommand("select * from t where name = '" + name + "'", connection); // refused: a command
                await using var command = new NpgsqlCommand("select * from t where name = @name", connection); // accepted: a parameter
                command.CommandText = $"select * from t where id = {id}"; // refused: a command's text
                command.CommandText = Constant; // accepted: a constant
                await context.Database.ExecuteSqlRawAsync("delete from t where name = {0}", name); // accepted: a parameter
                await context.Database.ExecuteSqlAsync($"delete from t where name = {name}"); // accepted: interpolation makes parameters
                _ = context.Set<Row>().FromSql($"select * from t where name = {name}"); // accepted: interpolation makes parameters
                await context.Database.ExecuteSqlRawAsync("select " + "1"); // accepted: constants joined are a constant
            }

            [SqlFromIdentifiers("The role name is checked against a pattern and quoted.")]
            public static async Task CreateRoleAsync(NpgsqlConnection connection, string role)
            {
                await using var command = new NpgsqlCommand($"CREATE ROLE \"{role}\"", connection); // accepted: a reviewed exemption
                await command.ExecuteNonQueryAsync();
            }

            [SqlFromIdentifiers(" ")]
            public static async Task DropRoleAsync(NpgsqlConnection connection, string role)
            {
                await using var command = new NpgsqlCommand($"DROP ROLE \"{role}\"", connection); // refused: an exemption without a reason
                await command.ExecuteNonQueryAsync();
            }
        }
        """;

    [Fact]
    [Proves("TF-071 AC3", "ADR-0053")]
    public async Task TheBuildRefusesSqlBuiltFromStringsAndAcceptsParametersConstantsAndAReasonedExemption()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "sql-fixtures", Ids.New().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var data = Path.Combine(Repository.Root, "src", "Kernel", "Iron.Kernel.Data", "Iron.Kernel.Data.csproj");
            var project = Path.Combine(folder, "Fixture.csproj");
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(project, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <IronLayer>Kernel</IronLayer>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{data}" />
                  </ItemGroup>
                </Project>
                """, ct);
            await File.WriteAllTextAsync(Path.Combine(folder, "Probe.cs"), Probe, ct);

            var (exitCode, output) = await DotnetCli.RunAsync(["build", project, "--configuration", DotnetCli.Configuration, "-nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-v:q"]);

            Assert.True(exitCode != 0, $"The build accepted SQL built from strings:{Environment.NewLine}{output}");

            // Only this analyzer's diagnostics count: the fixture may draw others (it awaits without ConfigureAwait).
            var refusals = Diagnostic().Matches(output).Select(match => (Line: int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), Id: match.Groups[2].Value)).ToHashSet();
            var lines = Probe.Split('\n');
            for (var number = 1; number <= lines.Length; number++)
            {
                var line = lines[number - 1];
                var refused = refusals.Contains((number, "IRON0010"));
                if (line.Contains("// refused: an exemption without a reason", StringComparison.Ordinal))
                {
                    // The exemption is itself an error, on its attribute three lines up, and its SQL is refused as well.
                    Assert.Contains((number - 3, "IRON0011"), refusals);
                    Assert.True(refused, $"Line {number} was not refused: {line.Trim()}");
                }
                else if (line.Contains("// refused", StringComparison.Ordinal))
                {
                    Assert.True(refused, $"Line {number} was not refused: {line.Trim()}{Environment.NewLine}{output}");
                }
                else if (line.Contains("// accepted", StringComparison.Ordinal))
                {
                    Assert.False(refused, $"Line {number} was refused: {line.Trim()}{Environment.NewLine}{output}");
                }
            }

            Assert.Contains("error IRON0010: The SQL text of ExecuteSqlRawAsync is built at run time", output, StringComparison.Ordinal);
            Assert.Contains("error IRON0010: The SQL text of new NpgsqlCommand is built at run time", output, StringComparison.Ordinal);
            Assert.Contains("error IRON0010: The SQL text of NpgsqlCommand.CommandText is built at run time", output, StringComparison.Ordinal);
            Assert.Contains("error IRON0010: The SQL text of NpgsqlDataSource.CreateCommand is built at run time", output, StringComparison.Ordinal);
            Assert.Contains("error IRON0011: [SqlFromIdentifiers] on 'DropRoleAsync' gives no reason", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [GeneratedRegex(@"Probe\.cs\((\d+),\d+\): error (IRON001[01])")]
    private static partial Regex Diagnostic();
}
