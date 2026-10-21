using Iron.Kernel;

namespace Iron.Architecture.Tests;

/// <summary>
/// The build's own layer check (target IronCheckLayer in Directory.Build.props). A fixture project written next to this
/// test's assembly inherits the repository's build settings, as every project does; the target must stop it without a
/// valid layer, or with a suite where none belongs, and let it through otherwise (ADR-0029, ADR-0043). Build output
/// folders are never read as projects (ProjectCatalog).
/// </summary>
public class LayerBuildCheckTests
{
    [Theory]
    [Proves("ADR-0029", "ADR-0043")]
    [InlineData(null, null, "IRON0001")]
    [InlineData("Kernal", null, "IRON0002")]
    [InlineData("kernel", null, "IRON0002")]
    [InlineData("Products", null, "IRON0002")]
    [InlineData("Kernel", null, null)]
    [InlineData("Suite", null, "IRON0003")]
    [InlineData("Business", "Procurement", "IRON0003")]
    [InlineData("Suite", "procurement", "IRON0004")]
    [InlineData("Suite", "Kernel", "IRON0004")]
    [InlineData("Suite", "Foundation", "IRON0004")]
    [InlineData("Suite", "Procurement", null)]
    public async Task TheBuildStopsAProjectWithoutAValidLayerOrSuite(string? layer, string? suite, string? expectedError)
    {
        var folder = NewFixtureFolder();
        try
        {
            var project = await WriteFixtureAsync(folder, layer, suite);

            var (exitCode, output) = await DotnetCli.RunAsync(["msbuild", project, "-t:IronCheckLayer", "-nologo", "-nodeReuse:false", "-v:q"]);

            if (expectedError is null)
            {
                Assert.True(exitCode == 0, output);
            }
            else
            {
                Assert.True(exitCode != 0, $"The build accepted IronLayer '{layer}' with IronSuite '{suite}'.");
                Assert.Contains($"error {expectedError}", output, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [Proves("ADR-0043")]
    [InlineData("Suite", "Procurement", true)]
    [InlineData("Business", null, false)]
    public async Task ASuiteProjectCarriesItsSuiteIntoItsAssembly(string layer, string? suite, bool carried)
    {
        var folder = NewFixtureFolder();
        try
        {
            var project = await WriteFixtureAsync(folder, layer, suite);

            var (exitCode, output) = await DotnetCli.RunAsync(["msbuild", project, "-getItem:AssemblyMetadata", "-nologo", "-nodeReuse:false"]);

            Assert.True(exitCode == 0, output);
            // IModule.Suite reads this item's attribute (SuiteName.Of), so the suite is written once, in the project file.
            Assert.Equal(carried, output.Contains($"\"Identity\": \"{SuiteName.MetadataKey}\"", StringComparison.Ordinal));
            if (carried)
            {
                Assert.Contains($"\"Value\": \"{suite}\"", output, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string NewFixtureFolder()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "layer-fixtures", Ids.New().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static async Task<string> WriteFixtureAsync(string folder, string? layer, string? suite)
    {
        var layerElement = layer is null ? string.Empty : $"<IronLayer>{layer}</IronLayer>";
        var suiteElement = suite is null ? string.Empty : $"<IronSuite>{suite}</IronSuite>";
        var project = Path.Combine(folder, "Fixture.csproj");
        await File.WriteAllTextAsync(project, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{layerElement}{suiteElement}</PropertyGroup></Project>", TestContext.Current.CancellationToken);
        return project;
    }
}
