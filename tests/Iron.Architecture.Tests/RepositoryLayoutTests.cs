using Iron.Kernel;

namespace Iron.Architecture.Tests;

/// <summary>Applies the layout and reference rules to the real repository.</summary>
public class RepositoryLayoutTests
{
    private static readonly IReadOnlyList<ProjectInfo> Projects = ProjectCatalog.Load(Repository.Root);

    [Fact]
    [Proves("ADR-0029", "ADR-0043")]
    public void EveryProjectDeclaresTheLayerItLivesIn()
    {
        ArchitectureAssert.NoViolations("Layers (ADR-0029)", LayerRules.Check(Projects));
    }

    [Fact]
    [Proves("ADR-0001", "ADR-0029", "ADR-0043")]
    public void EveryProjectReferenceFollowsTheModuleBoundaryRules()
    {
        ArchitectureAssert.NoViolations("Module boundary rules (ADR-0001, ADR-0029)", ModuleBoundaryRules.Check(Projects));
    }

    [Fact]
    [Proves("ADR-0043")]
    public void ProjectNamesAreUnique()
    {
        var duplicates = Projects
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} appears {group.Count()} times.")
            .ToList();

        ArchitectureAssert.NoViolations("Unique project names", duplicates);
    }

    [Fact]
    [Proves("ADR-0001", "ADR-0029")]
    public void EveryProjectLivesInAFolderWithItsOwnName()
    {
        // src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj and src/Kernel/Iron.Kernel.Identity/Contracts/Iron.Kernel.Identity.Contracts.csproj
        var violations = new List<string>();
        foreach (var project in Projects)
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(project.RelativePath)) ?? string.Empty;
            var expected = project.Kind == ProjectKind.Contracts ? "Contracts" : project.Name;
            if (!string.Equals(folder, expected, StringComparison.Ordinal))
            {
                violations.Add($"{project.RelativePath}: expected a folder named '{expected}', found '{folder}'.");
            }
        }

        ArchitectureAssert.NoViolations("Project folder names", violations);
    }

    [Fact]
    [Proves("ADR-0001")]
    public void EveryContractsProjectLivesInsideItsModule()
    {
        var violations = new List<string>();
        foreach (var project in Projects.Where(p => p.Kind == ProjectKind.Contracts))
        {
            var contractsFolder = Path.GetDirectoryName(project.RelativePath);
            var moduleFolder = Path.GetFileName(Path.GetDirectoryName(contractsFolder)) ?? string.Empty;
            if (!string.Equals(moduleFolder, project.ModuleName, StringComparison.Ordinal))
            {
                violations.Add($"{project.RelativePath}: expected to be inside '{project.ModuleName}', found '{moduleFolder}'.");
            }
        }

        ArchitectureAssert.NoViolations("Contracts location", violations);
    }

    [Fact]
    [Proves("ADR-0001")]
    public void EveryModuleHasAContractsProject()
    {
        var contracts = Projects.Where(p => p.Kind == ProjectKind.Contracts).Select(p => p.ModuleName).ToHashSet(StringComparer.Ordinal);
        var violations = Projects
            .Where(p => p.Kind == ProjectKind.Module)
            .Where(p => !contracts.Contains(p.Name))
            .Select(p => $"{p.RelativePath}: no Contracts project found ({p.Name}.Contracts).")
            .ToList();

        ArchitectureAssert.NoViolations("Contracts project per module", violations);
    }

    [Fact]
    [Proves("ADR-0029", "ADR-0043")]
    public void ProjectsUnderSrcUseTheIronPrefix()
    {
        var violations = Projects
            .Where(p => p.RelativePath.StartsWith("src/", StringComparison.Ordinal))
            .Where(p => !p.Name.StartsWith("Iron.", StringComparison.Ordinal))
            .Select(p => $"{p.RelativePath}: projects under src/ are named Iron.<Name>.")
            .ToList();

        ArchitectureAssert.NoViolations("Iron prefix", violations);
    }

    [Theory]
    [Proves("TF-190 AC4")]
    [InlineData("src")]
    [InlineData("tests")]
    [InlineData("tools")]
    [InlineData("shell")]
    [InlineData("infra")]
    [InlineData("docs")]
    public void TheRepositoryHasTheExpectedTopLevelFolders(string folder)
    {
        Assert.True(Directory.Exists(Path.Combine(Repository.Root, folder)), $"Folder '{folder}' is missing at the repository root.");
    }

    [Theory]
    [Proves("ADR-0029", "ADR-0043")]
    [InlineData("src/Kernel")]
    [InlineData("src/Business")]
    [InlineData("src/Suites")]
    [InlineData("src/Suites/Procurement")]
    [InlineData("src/Hosts")]
    public void SourceHasAFolderPerLayerAndOneForTheHosts(string folder)
    {
        Assert.True(Directory.Exists(Path.Combine(Repository.Root, folder)), $"Folder '{folder}' is missing (ADR-0029, ADR-0043).");
    }

    [Fact]
    [Proves("ADR-0043")]
    public void EveryFolderUnderTheSuitesIsASuite()
    {
        var wrong = Directory.EnumerateDirectories(Path.Combine(Repository.Root, "src", "Suites"))
            .Select(directory => Path.GetFileName(directory))
            .Where(name => !SuiteName.IsValid(name) || !File.Exists(Path.Combine(Repository.Root, "src", "Suites", name, "README.md")))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Build output can leave a folder behind; a suite folder is named as the suite and says what the suite is.
        Assert.True(wrong.Count == 0, $"src/Suites/ holds one folder per suite, named in PascalCase, each with a README.md (ADR-0043). Fix: {string.Join(", ", wrong.Select(name => "src/Suites/" + name))}.");
    }

    [Fact]
    [Proves("ADR-0029", "ADR-0043")]
    public void NothingButTheLayerFoldersSitsInSource()
    {
        string[] expected = ["Business", "Hosts", "Kernel", "Suites"];
        var others = Directory.EnumerateDirectories(Path.Combine(Repository.Root, "src"))
            .Select(directory => Path.GetFileName(directory))
            .Where(name => !expected.Contains(name, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        // A checkout of a branch from before ADR-0029 leaves build output in folders such as src/Iron.Identity.
        Assert.True(others.Count == 0, $"src/ holds the layer folders and src/Hosts only (ADR-0029, ADR-0043). Move or delete: {string.Join(", ", others.Select(name => "src/" + name))}.");
    }
}
