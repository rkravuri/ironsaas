using System.Xml.Linq;

namespace Iron.Architecture.Tests;

/// <summary>
/// Finds every project file in the repository and classifies it by its location.
/// The classification is pure (path and XML in, description out), so it is unit tested with made-up paths.
/// </summary>
public static class ProjectCatalog
{
    /// <summary>The shared libraries. Every module may reference them; they reference each other and nothing else.</summary>
    public static readonly string[] SharedLibraries =
    [
        "Iron.Kernel",
        "Iron.Kernel.Data",
        "Iron.Kernel.Api",
        "Iron.Kernel.Telemetry",
        "Iron.Kernel.Security",
    ];

    /// <summary>Top-level folders that may contain projects.</summary>
    public static readonly string[] ScannedFolders = ["src", "products", "tests", "tools"];

    private const string ContractsSuffix = ".Contracts";

    public static IReadOnlyList<ProjectInfo> Load(string repositoryRoot)
    {
        var projects = new List<ProjectInfo>();
        foreach (var folder in ScannedFolders)
        {
            var folderPath = Path.Combine(repositoryRoot, folder);
            if (!Directory.Exists(folderPath))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folderPath, "*.csproj", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
                projects.Add(Describe(relativePath, File.ReadAllText(file)));
            }
        }

        return projects.OrderBy(p => p.RelativePath, StringComparer.Ordinal).ToList();
    }

    /// <summary>Describes one project from its relative path and its XML content.</summary>
    public static ProjectInfo Describe(string relativePath, string projectXml)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var segments = relativePath.Split('/');
        var (kind, moduleName) = Classify(name, segments);
        return new ProjectInfo(name, relativePath, kind, moduleName, ReadProjectReferences(projectXml));
    }

    private static (ProjectKind Kind, string? ModuleName) Classify(string name, string[] segments)
    {
        var topFolder = segments[0];
        if (topFolder == "tests")
        {
            return (ProjectKind.Test, null);
        }

        if (topFolder == "tools")
        {
            return (ProjectKind.Tool, null);
        }

        // src/Kernel/Iron.Kernel.Identity/Contracts/Iron.Kernel.Identity.Contracts.csproj belongs to module Iron.Kernel.Identity.
        // products/Procurement/Contracts/Procurement.Contracts.csproj belongs to product Procurement.
        if (name.EndsWith(ContractsSuffix, StringComparison.Ordinal))
        {
            return (ProjectKind.Contracts, name[..^ContractsSuffix.Length]);
        }

        if (topFolder == "products")
        {
            return (ProjectKind.Product, name);
        }

        if (name.StartsWith("Iron.Host.", StringComparison.Ordinal))
        {
            return (ProjectKind.Host, null);
        }

        if (SharedLibraries.Contains(name, StringComparer.Ordinal))
        {
            return (ProjectKind.Shared, null);
        }

        return (ProjectKind.Module, name);
    }

    private static IReadOnlyList<string> ReadProjectReferences(string projectXml)
    {
        var document = XDocument.Parse(projectXml);
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
}
