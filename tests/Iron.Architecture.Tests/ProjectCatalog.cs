using System.Xml.Linq;

namespace Iron.Architecture.Tests;

/// <summary>
/// Finds every project file in the repository and describes it by its declared layer and its name.
/// The description is pure (path and XML in, description out), so it is unit tested with made-up paths.
/// </summary>
public static class ProjectCatalog
{
    /// <summary>The kernel vocabulary. It references nothing; every other layered project may reference it.</summary>
    public const string Vocabulary = "Iron.Kernel";

    /// <summary>The kernel libraries. Every module may reference them; they reference the vocabulary and each other.</summary>
    public static readonly string[] KernelLibraries =
    [
        "Iron.Kernel.Data",
        "Iron.Kernel.Api",
        "Iron.Kernel.Telemetry",
        "Iron.Kernel.Security",
    ];

    /// <summary>Folders never searched for projects: build output, packages and anything hidden.</summary>
    public static readonly string[] SkippedFolders = ["bin", "obj", "node_modules"];

    private const string ContractsSuffix = ".Contracts";

    /// <summary>Every project file under the repository root, wherever it is, so that a project in an unexpected folder is still checked.</summary>
    public static IReadOnlyList<ProjectInfo> Load(string repositoryRoot)
    {
        var projects = new List<ProjectInfo>();
        foreach (var file in ProjectFiles(repositoryRoot))
        {
            var relativePath = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
            projects.Add(Describe(relativePath, File.ReadAllText(file)));
        }

        return projects.OrderBy(p => p.RelativePath, StringComparer.Ordinal).ToList();
    }

    /// <summary>Describes one project from its relative path and its XML content.</summary>
    public static ProjectInfo Describe(string relativePath, string projectXml)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var document = XDocument.Parse(projectXml);
        var declaredLayer = ReadLayer(document);
        ProjectLayer? layer = ProjectLayers.TryParse(declaredLayer, out var parsed) ? parsed : null;
        var (kind, moduleName) = Classify(name, layer);
        return new ProjectInfo(name, relativePath, declaredLayer, layer, kind, moduleName, ReadProjectReferences(document), ReadProperty(document, "IronSuite"));
    }

    private static (ProjectKind Kind, string? ModuleName) Classify(string name, ProjectLayer? layer)
    {
        switch (layer)
        {
            case null:
                return (ProjectKind.Unknown, null);
            case ProjectLayer.Host:
                return (ProjectKind.Host, null);
            case ProjectLayer.Test:
                return (ProjectKind.Test, null);
            case ProjectLayer.Tool:
                return (ProjectKind.Tool, null);
        }

        // src/Kernel/Iron.Kernel.Identity/Contracts/Iron.Kernel.Identity.Contracts.csproj belongs to module Iron.Kernel.Identity.
        if (name.EndsWith(ContractsSuffix, StringComparison.Ordinal))
        {
            return (ProjectKind.Contracts, name[..^ContractsSuffix.Length]);
        }

        if (layer == ProjectLayer.Kernel && name == Vocabulary)
        {
            return (ProjectKind.Vocabulary, null);
        }

        if (layer == ProjectLayer.Kernel && KernelLibraries.Contains(name, StringComparer.Ordinal))
        {
            return (ProjectKind.Library, null);
        }

        return (ProjectKind.Module, name);
    }

    private static string? ReadLayer(XDocument document) => ReadProperty(document, "IronLayer");

    private static string? ReadProperty(XDocument document, string property)
    {
        // An empty element is no value, as the build's own check sees it; the value is not trimmed, so " Kernel " is refused by both.
        var element = document.Descendants().FirstOrDefault(e => e.Name.LocalName == property);
        return element is null || element.Value.Length == 0 ? null : element.Value;
    }

    private static IReadOnlyList<string> ReadProjectReferences(XDocument document)
    {
        var references = new List<string>();
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName != "ProjectReference")
            {
                continue;
            }

            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            references.Add(Path.GetFileNameWithoutExtension(include.Replace('\\', '/')));
        }

        return references;
    }

    private static IEnumerable<string> ProjectFiles(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*.csproj"))
        {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(folder))
        {
            var childName = Path.GetFileName(child);
            if (childName.StartsWith('.') || SkippedFolders.Contains(childName, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var file in ProjectFiles(child))
            {
                yield return file;
            }
        }
    }
}
