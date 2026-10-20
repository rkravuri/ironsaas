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

            violations.AddRange(ForbiddenMemberRules.Check(assemblyPath));
        }

        ArchitectureAssert.NoViolations("Assemblies missing from the check", missing);
        ArchitectureAssert.NoViolations("System clock rule (ADR-0007)", violations);
    }
}
