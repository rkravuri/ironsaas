namespace Iron.Architecture.Tests;

/// <summary>Applies the layout and reference rules to the real repository.</summary>
public class RepositoryLayoutTests
{
    private static readonly IReadOnlyList<ProjectInfo> Projects = ProjectCatalog.Load(Repository.Root);

    [Fact]
    public void EveryProjectReferenceFollowsTheModuleBoundaryRules()
    {
        ArchitectureAssert.NoViolations("Module boundary rules (ADR-0001)", ModuleBoundaryRules.Check(Projects));
    }

    [Fact]
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
    public void EveryModuleHasAContractsProject()
    {
        var contracts = Projects.Where(p => p.Kind == ProjectKind.Contracts).Select(p => p.ModuleName).ToHashSet(StringComparer.Ordinal);
        var violations = Projects
            .Where(p => p.Kind is ProjectKind.Module or ProjectKind.Product)
            .Where(p => !contracts.Contains(p.Name))
            .Select(p => $"{p.RelativePath}: no Contracts project found ({p.Name}.Contracts).")
            .ToList();

        ArchitectureAssert.NoViolations("Contracts project per module", violations);
    }

    [Fact]
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
    [InlineData("src")]
    [InlineData("tests")]
    [InlineData("products")]
    [InlineData("tools")]
    [InlineData("infra")]
    [InlineData("docs")]
    public void TheRepositoryHasTheExpectedTopLevelFolders(string folder)
    {
        Assert.True(Directory.Exists(Path.Combine(Repository.Root, folder)), $"Folder '{folder}' is missing at the repository root.");
    }
}
