namespace Iron.Architecture.Tests;

/// <summary>Made-up project files for the rule tests, so that every refusal is proven without touching the repository.</summary>
internal static class Fixture
{
    private const string SuitesFolder = "src/Suites/";

    /// <summary>
    /// A project at this path that declares the layer its folder belongs to, with these project references. Under
    /// <c>src/Suites/&lt;Suite&gt;/</c> it also declares that suite, as a suite project must (ADR-0043).
    /// </summary>
    public static ProjectInfo At(string relativePath, params string[] references) =>
        DeclaringSuite(relativePath, LayerOfFolder(relativePath), SuiteOfFolder(relativePath), references);

    /// <summary>A project that declares exactly this layer (null: no IronLayer at all) and no suite, with these project references.</summary>
    public static ProjectInfo Declaring(string relativePath, string? layer, params string[] references) =>
        DeclaringSuite(relativePath, layer, suite: null, references);

    /// <summary>A project that declares exactly this layer and this suite (null: none), with these project references.</summary>
    public static ProjectInfo DeclaringSuite(string relativePath, string? layer, string? suite, params string[] references)
    {
        var layerElement = layer is null ? string.Empty : $"<IronLayer>{layer}</IronLayer>";
        var suiteElement = suite is null ? string.Empty : $"<IronSuite>{suite}</IronSuite>";
        var properties = layer is null && suite is null ? string.Empty : $"<PropertyGroup>{layerElement}{suiteElement}</PropertyGroup>";
        var items = string.Concat(references.Select(r => $"<ProjectReference Include=\"../{r}/{r}.csproj\" />"));
        var xml = $"<Project Sdk=\"Microsoft.NET.Sdk\">{properties}<ItemGroup>{items}</ItemGroup></Project>";
        return ProjectCatalog.Describe(relativePath, xml);
    }

    private static string LayerOfFolder(string relativePath) => relativePath switch
    {
        _ when relativePath.StartsWith("src/Kernel/", StringComparison.Ordinal) => "Kernel",
        _ when relativePath.StartsWith("src/Business/", StringComparison.Ordinal) => "Business",
        _ when relativePath.StartsWith(SuitesFolder, StringComparison.Ordinal) => "Suite",
        _ when relativePath.StartsWith("src/Hosts/", StringComparison.Ordinal) => "Host",
        _ when relativePath.StartsWith("tests/", StringComparison.Ordinal) => "Test",
        _ when relativePath.StartsWith("tools/", StringComparison.Ordinal) => "Tool",
        _ => throw new ArgumentException($"No layer folder in '{relativePath}'; use Declaring.", nameof(relativePath)),
    };

    /// <summary>The <c>&lt;Suite&gt;</c> of <c>src/Suites/&lt;Suite&gt;/...</c>, or null outside the suites.</summary>
    private static string? SuiteOfFolder(string relativePath) =>
        relativePath.StartsWith(SuitesFolder, StringComparison.Ordinal) ? relativePath[SuitesFolder.Length..].Split('/')[0] : null;
}
