namespace Iron.Architecture.Tests;

public class ForbiddenMemberRulesTests
{
    [Fact]
    [Proves("ADR-0007")]
    public void ReportsDirectReadsOfTheSystemClock()
    {
        var members = new List<(string Namespace, string TypeName, string MemberName)>
        {
            ("System", "DateTime", "get_UtcNow"),
            ("System", "DateTimeOffset", "get_Now"),
            ("System", "DateTime", "get_Kind"),
            ("System", "TimeProvider", "GetUtcNow"),
            ("System", "Guid", "CreateVersion7"),
        };

        var violations = ForbiddenMemberRules.FindForbidden("Iron.Sample.dll", members);

        Assert.Collection(
            violations,
            first => Assert.Contains("System.DateTime.UtcNow", first, StringComparison.Ordinal),
            second => Assert.Contains("System.DateTimeOffset.Now", second, StringComparison.Ordinal));
    }

    [Fact]
    [Proves("ADR-0007")]
    public void ReadsMemberReferencesFromACompiledAssembly()
    {
        var members = ForbiddenMemberRules.ReadReferencedMembers(typeof(ForbiddenMemberRules).Assembly.Location);

        Assert.Contains(members, m => m.Namespace == "System" && m.TypeName == "String");
    }

    [Fact]
    [Proves("ADR-0007")]
    public void NoFoundationAssemblyReadsTheSystemClockDirectly()
    {
        var projects = ProjectCatalog.Load(Repository.Root).Where(p => p.Kind is not (ProjectKind.Test or ProjectKind.Tool or ProjectKind.Unknown));

        var violations = new List<string>();
        var missing = new List<string>();
        foreach (var project in projects)
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, project.Name + ".dll");
            if (!File.Exists(assemblyPath))
            {
                missing.Add($"{project.RelativePath}: add a ProjectReference to it in tests/Iron.Architecture.Tests so its assembly can be checked.");
                continue;
            }

            violations.AddRange(ForbiddenMemberRules.Check(Built(project) ?? assemblyPath));
        }

        ArchitectureAssert.NoViolations("Assemblies missing from the check", missing);
        ArchitectureAssert.NoViolations("System clock rule (ADR-0007)", violations);
    }

    /// <summary>
    /// The project's own build output, the assembly that ships. A run with coverage (ADR-0055) instruments the copies
    /// beside this test, and coverlet's tracker in each copy reads the system clock; the build output stays as compiled.
    /// </summary>
    private static string? Built(ProjectInfo project)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Release";
        var folder = Path.GetDirectoryName(Path.Combine(Repository.Root, project.RelativePath))!;
        var path = Path.Combine(folder, "bin", configuration, "net10.0", project.Name + ".dll");
        return File.Exists(path) ? path : null;
    }
}
