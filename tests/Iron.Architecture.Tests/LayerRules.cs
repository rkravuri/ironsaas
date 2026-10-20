namespace Iron.Architecture.Tests;

/// <summary>
/// Every project declares its layer, the layer is one of the six, and it agrees with the project's name and folder
/// (ADR-0029). A project cannot change its layer by declaring another one.
/// </summary>
public static class LayerRules
{
    private const string Layers = "Kernel, Business, Products, Host, Test or Tool";

    public static IReadOnlyList<string> Check(IReadOnlyList<ProjectInfo> projects)
    {
        var violations = new List<string>();
        foreach (var project in projects)
        {
            var reason = ReasonIfWrong(project);
            if (reason is not null)
            {
                violations.Add($"{project.RelativePath}: {reason}");
            }
        }

        return violations;
    }

    /// <summary>Returns null when the project's layer is declared and fits, otherwise what is wrong.</summary>
    public static string? ReasonIfWrong(ProjectInfo project)
    {
        if (project.DeclaredLayer is null)
        {
            return $"declares no <IronLayer>. Add one to its first PropertyGroup: {Layers} (ADR-0029).";
        }

        if (project.Layer is not { } layer)
        {
            return $"declares IronLayer '{project.DeclaredLayer}', which is not a layer. Use {Layers} (ADR-0029).";
        }

        var (folder, fits, naming) = Expectation(layer, project.Name);
        if (!project.RelativePath.StartsWith(folder, StringComparison.Ordinal) || !fits)
        {
            return $"declares IronLayer {layer}, but a {layer} project is {naming} and lives under {folder} (ADR-0029).";
        }

        return null;
    }

    private static (string Folder, bool NameFits, string Naming) Expectation(ProjectLayer layer, string name) => layer switch
    {
        ProjectLayer.Kernel => ("src/Kernel/", name == ProjectCatalog.Vocabulary || name.StartsWith("Iron.Kernel.", StringComparison.Ordinal), "named Iron.Kernel or Iron.Kernel.*"),
        ProjectLayer.Business => ("src/Business/", name.StartsWith("Iron.Business.", StringComparison.Ordinal), "named Iron.Business.*"),
        ProjectLayer.Products => ("src/Products/", name.StartsWith("Iron.Products.", StringComparison.Ordinal), "named Iron.Products.*"),
        ProjectLayer.Host => ("src/Hosts/", name.StartsWith("Iron.Host.", StringComparison.Ordinal), "named Iron.Host.*"),
        // A test project, or a library the test projects share (the platform services they run on: tests/Iron.Testing).
        ProjectLayer.Test => ("tests/", name.EndsWith(".Tests", StringComparison.Ordinal) || name.EndsWith(".Testing", StringComparison.Ordinal), "named *.Tests, or *.Testing for a library the tests share"),
        ProjectLayer.Tool => ("tools/", true, "any name"),
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Every layer has an expectation."),
    };
}
