namespace Iron.Architecture.Tests;

/// <summary>The layers a project can declare with &lt;IronLayer&gt; in its project file (ADR-0029).</summary>
public enum ProjectLayer
{
    Kernel,
    Business,
    Products,
    Host,
    Test,
    Tool,
}

public static class ProjectLayers
{
    /// <summary>The value as written in a project file, compared case-sensitively as the build's own check does.</summary>
    public static bool TryParse(string? value, out ProjectLayer layer)
    {
        foreach (var candidate in Enum.GetValues<ProjectLayer>())
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.Ordinal))
            {
                layer = candidate;
                return true;
            }
        }

        layer = default;
        return false;
    }

    /// <summary>Kernel, Business and Products are ordered; dependencies go down that order only. The rest have no rank.</summary>
    public static int? Rank(ProjectLayer layer) => layer switch
    {
        ProjectLayer.Kernel => 0,
        ProjectLayer.Business => 1,
        ProjectLayer.Products => 2,
        _ => null,
    };

    /// <summary>The prefix of a module schema in this layer, for example <c>kernel_</c>; null for layers without schemas.</summary>
    public static string? SchemaPrefix(ProjectLayer layer) => layer switch
    {
        ProjectLayer.Kernel => "kernel_",
        ProjectLayer.Business => "business_",
        ProjectLayer.Products => "products_",
        _ => null,
    };
}
