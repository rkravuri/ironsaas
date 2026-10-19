using System.Globalization;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Settings.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Iron.Kernel.Settings.Tests;

/// <summary>
/// TF-059 AC1 and ADR-0036: the typed definition is the accessor, so a key nobody declared, a key written as text, and a
/// value read as another type are compile errors, not review comments. Each test compiles a few lines against the
/// vocabulary and the contracts, as a module's own code is compiled.
/// </summary>
public class UndeclaredKeyTests
{
    private const string Usings = """
        using System.Threading;
        using System.Threading.Tasks;
        using Iron.Kernel;
        using Iron.Kernel.Identity.Contracts;
        using Iron.Kernel.Settings.Contracts;

        """;

    [Fact]
    public void ReadingAKeyNobodyDeclaredDoesNotCompile()
    {
        var errors = Compile("""
            public static class Probe
            {
                public static Task<int> Read(ISettings settings) => settings.GetAsync(IdentitySettings.SessionIdleHours, SettingContext.Tenant, CancellationToken.None);
            }
            """);

        var error = Assert.Single(errors);
        Assert.Equal("CS0117", error.Id);
        Assert.Contains("SessionIdleHours", error.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyWrittenAsTextDoesNotCompile()
    {
        var errors = Compile("""
            public static class Probe
            {
                public static Task<int> Read(ISettings settings) => settings.GetAsync("identity.session.idle_minutes", SettingContext.Tenant, CancellationToken.None);
            }
            """);

        Assert.Contains(errors, error => error.Id is "CS0411" or "CS1503");
    }

    [Fact]
    public void AValueReadAsAnotherTypeDoesNotCompile()
    {
        var errors = Compile("""
            public static class Probe
            {
                public static Task<string> Read(ISettings settings) => settings.GetAsync(IdentitySettings.SessionIdleMinutes, SettingContext.Tenant, CancellationToken.None);
            }
            """);

        Assert.Contains(errors, error => error.Id is "CS0029" or "CS0266" or "CS1662");
    }

    [Fact]
    public void ADeclaredKeyReadAsItsTypeCompiles()
    {
        var errors = Compile("""
            public static class Probe
            {
                public static Task<int> Read(ISettings settings) => settings.GetAsync(IdentitySettings.SessionIdleMinutes, SettingContext.Tenant, CancellationToken.None);

                public static Task<string> Scheme(ISettings settings, System.Guid person) => settings.GetAsync(ShellSettings.ColourScheme, SettingContext.ForPerson(person), CancellationToken.None);
            }
            """);

        Assert.Empty(errors);
    }

    private static List<Diagnostic> Compile(string source)
    {
        // The assemblies this test runs with: the framework, the vocabulary, and the two contracts a module would use.
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(Setting<>).Assembly.Location)
            .Append(typeof(ISettings).Assembly.Location)
            .Append(typeof(IdentitySettings).Assembly.Location)
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
