using Iron.Kernel;

namespace Iron.Architecture.Tests;

/// <summary>
/// Every project declares its layer, the layer is one of the six, and it agrees with the project's name and folder
/// (ADR-0029). A Suite project also declares its suite, which agrees with its name and folder, and no other project
/// declares one (ADR-0043). A project cannot change its layer or its suite by declaring another one.
/// </summary>
public static class LayerRules
{
    private const string Layers = "Kernel, Business, Suite, Host, Test or Tool";

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

    /// <summary>Returns null when the project's layer (and suite) is declared and fits, otherwise what is wrong.</summary>
    public static string? ReasonIfWrong(ProjectInfo project)
    {
        if (project.DeclaredLayer is null)
        {
            return $"declares no <IronLayer>. Add one to its first PropertyGroup: {Layers} (ADR-0029).";
        }

        if (project.Layer is not { } layer)
        {
            return $"declares IronLayer '{project.DeclaredLayer}', which is not a layer. Use {Layers} (ADR-0029, ADR-0043).";
        }

        if (layer == ProjectLayer.Suite)
        {
            return SuiteReasonIfWrong(project);
        }

        if (project.DeclaredSuite is not null)
        {
            return $"declares IronSuite '{project.DeclaredSuite}', but it is a {layer} project. Only a Suite project names its suite; remove it (ADR-0043).";
        }

        var (folder, fits, naming) = Expectation(layer, project.Name);
        if (!project.RelativePath.StartsWith(folder, StringComparison.Ordinal) || !fits)
        {
            return $"declares IronLayer {layer}, but a {layer} project is {naming} and lives under {folder} (ADR-0029).";
        }

        return null;
    }

    /// <summary>A Suite project names a valid suite, and is named and placed after it: Iron.Suites.Procurement.* in src/Suites/Procurement/.</summary>
    private static string? SuiteReasonIfWrong(ProjectInfo project)
    {
        var suite = project.DeclaredSuite;
        if (suite is null)
        {
            return "declares IronLayer Suite but no <IronSuite>. Add the suite's name beside it, for example <IronSuite>Procurement</IronSuite> (ADR-0043).";
        }

        if (!SuiteName.IsValid(suite))
        {
            return $"declares IronSuite '{suite}', which is not a suite name. Use one word in PascalCase, other than Kernel, Business and Foundation (ADR-0043).";
        }

        var folder = $"src/Suites/{suite}/";
        var prefix = $"Iron.Suites.{suite}.";
        if (!project.RelativePath.StartsWith(folder, StringComparison.Ordinal) || !project.Name.StartsWith(prefix, StringComparison.Ordinal) || project.Name.Length == prefix.Length)
        {
            return $"declares IronLayer Suite and IronSuite {suite}, but a project of that suite is named {prefix}* and lives under {folder} (ADR-0043).";
        }

        return null;
    }

    private static (string Folder, bool NameFits, string Naming) Expectation(ProjectLayer layer, string name) => layer switch
    {
        ProjectLayer.Kernel => ("src/Kernel/", name == ProjectCatalog.Vocabulary || name.StartsWith("Iron.Kernel.", StringComparison.Ordinal), "named Iron.Kernel or Iron.Kernel.*"),
        ProjectLayer.Business => ("src/Business/", name.StartsWith("Iron.Business.", StringComparison.Ordinal), "named Iron.Business.*"),
        ProjectLayer.Host => ("src/Hosts/", name.StartsWith("Iron.Host.", StringComparison.Ordinal), "named Iron.Host.*"),
        // A test project, a library the test projects share (the platform services they run on and what a test proves:
        // tests/Iron.Testing), or the analyzers that check them (tests/Iron.Testing.Analyzers, ADR-0044).
        ProjectLayer.Test => ("tests/", name.EndsWith(".Tests", StringComparison.Ordinal) || name.EndsWith(".Testing", StringComparison.Ordinal) || name.EndsWith(".Testing.Analyzers", StringComparison.Ordinal), "named *.Tests, *.Testing for a library the tests share, or *.Testing.Analyzers for the analyzers that check them"),
        ProjectLayer.Tool => ("tools/", true, "any name"),
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Every layer but Suite has an expectation here; Suite has its own."),
    };
}
