namespace Iron.Architecture.Tests;

/// <summary>Applies the private-namespace rule to the compiled assemblies of the repository.</summary>
public class AssemblyReferenceTests
{
    [Fact]
    public void NoAssemblyUsesAnotherModulesPrivateTypes()
    {
        var projects = ProjectCatalog.Load(Repository.Root);
        var modules = projects
            .Where(p => p.Kind == ProjectKind.Module)
            .Select(p => p.Name)
            .ToList();

        var violations = new List<string>();
        var missing = new List<string>();
        foreach (var project in projects.Where(p => p.Kind is ProjectKind.Module or ProjectKind.Contracts or ProjectKind.Host))
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, project.Name + ".dll");
            if (!File.Exists(assemblyPath))
            {
                missing.Add($"{project.RelativePath}: add a ProjectReference to it in tests/Iron.Architecture.Tests so its assembly can be checked.");
                continue;
            }

            violations.AddRange(AssemblyReferenceRules.Check(assemblyPath, project.ModuleName, modules));
        }

        ArchitectureAssert.NoViolations("Assemblies missing from the check", missing);
        ArchitectureAssert.NoViolations("Private namespace rule (ADR-0001)", violations);
    }
}
