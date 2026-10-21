using System.Globalization;
using Iron.Kernel.Data.Policies;
using Iron.Kernel.Data.Tests.Sample;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Iron.Kernel.Data.Tests.Policies;

/// <summary>
/// ADR-0032 and TF-035 AC3: forgetting a business record's policy is a compile error, not a review comment. Each test
/// compiles a few lines against Iron.Kernel.Data and the sample module, as a module's own code would be compiled.
/// </summary>
public class PagingNeedsAPolicyTests
{
    private const string Usings = """
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using Iron.Kernel;
        using Iron.Kernel.Data.Lists;
        using Iron.Kernel.Data.Policies;
        using Iron.Kernel.Data.Tests.Sample;

        """;

    [Fact]
    [Proves("TF-035 AC3", "ADR-0032")]
    public void PagingARawQueryOfABusinessRecordDoesNotCompile()
    {
        var errors = Compile("""
            public static class Probe
            {
                public static Task<Page<Widget>> Raw(IQueryable<Widget> widgets, PageRequest page) => widgets.ToPageAsync(page, CancellationToken.None);
            }
            """);

        var error = Assert.Single(errors);
        Assert.Equal("CS0311", error.Id);
        Assert.Contains("IOwnAccessRule", error.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0032")]
    public void APolicyQueryCannotBeMadeOutsideThePolicies()
    {
        var errors = Compile("""
            public static class Probe
            {
                public static PolicyQuery<Widget> Forged(IQueryable<Widget> widgets) => new PolicyQuery<Widget>(widgets);
            }
            """);

        Assert.Contains(errors, error => error.Id is "CS0122" or "CS1729");
    }

    [Fact]
    [Proves("ADR-0032")]
    public void PagingThroughThePolicyAndPagingTenantWideDataCompile()
    {
        var errors = Compile("""
            public static class Probe
            {
                public static async Task<Page<Widget>> Policed(IRecordPolicies policies, IQueryable<Widget> widgets, PageRequest page) =>
                    await (await policies.ApplyAsync(widgets, CancellationToken.None)).ToPageAsync(page, CancellationToken.None);

                public static Task<Page<Preference>> TenantWide(IQueryable<Preference> preferences, PageRequest page) =>
                    preferences.ToPageAsync(page, CancellationToken.None);
            }
            """);

        Assert.Empty(errors);
    }

    private static List<Diagnostic> Compile(string source)
    {
        // The assemblies this test runs with: the framework, Entity Framework, Iron.Kernel.Data and the sample module.
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(Widget).Assembly.Location)
            .Append(typeof(PolicyQuery<>).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Probe",
            [CSharpSyntaxTree.ParseText(Usings + source, cancellationToken: TestContext.Current.CancellationToken)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        return compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    }
}
