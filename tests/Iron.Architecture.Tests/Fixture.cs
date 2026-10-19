namespace Iron.Architecture.Tests;

/// <summary>Made-up project files for the rule tests, so that every refusal is proven without touching the repository.</summary>
internal static class Fixture
{
    /// <summary>A project at this path that declares the layer its folder belongs to, with these project references.</summary>
    public static ProjectInfo At(string relativePath, params string[] references) =>
        Declaring(relativePath, LayerOfFolder(relativePath), references);

    /// <summary>A project that declares exactly this layer (null: no IronLayer at all), with these project references.</summary>
    public static ProjectInfo Declaring(string relativePath, string? layer, params string[] references)
    {
        var layerElement = layer is null ? string.Empty : $"<PropertyGroup><IronLayer>{layer}</IronLayer></PropertyGroup>";
        var items = string.Concat(references.Select(r => $"<ProjectReference Include=\"../{r}/{r}.csproj\" />"));
        var xml = $"<Project Sdk=\"Microsoft.NET.Sdk\">{layerElement}<ItemGroup>{items}</ItemGroup></Project>";
        return ProjectCatalog.Describe(relativePath, xml);
    }

    private static string LayerOfFolder(string relativePath) => relativePath switch
    {
        _ when relativePath.StartsWith("src/Kernel/", StringComparison.Ordinal) => "Kernel",
        _ when relativePath.StartsWith("src/Business/", StringComparison.Ordinal) => "Business",
        _ when relativePath.StartsWith("src/Products/", StringComparison.Ordinal) => "Products",
        _ when relativePath.StartsWith("src/Hosts/", StringComparison.Ordinal) => "Host",
        _ when relativePath.StartsWith("tests/", StringComparison.Ordinal) => "Test",
        _ when relativePath.StartsWith("tools/", StringComparison.Ordinal) => "Tool",
        _ => throw new ArgumentException($"No layer folder in '{relativePath}'; use Declaring.", nameof(relativePath)),
    };
}
