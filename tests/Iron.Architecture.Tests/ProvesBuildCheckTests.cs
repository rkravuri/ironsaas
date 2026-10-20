using Iron.Kernel;

namespace Iron.Architecture.Tests;

/// <summary>
/// The build refuses a test method that does not name what it proves (ADR-0044). A fixture test project written next to
/// this test's assembly inherits the repository's build settings, as every test project does, so Directory.Build.props
/// puts the analyzer of tests/Iron.Testing.Analyzers into its compile; it uses the real xUnit and Iron.Testing attributes.
/// </summary>
public class ProvesBuildCheckTests
{
    private const string Probe = """
        using Iron.Testing;
        using Xunit;

        namespace Fixture;

        public class ProbeTests
        {
            [Fact]
            public void Untagged()
            {
            }

            [Theory]
            [InlineData(1)]
            public void UntaggedTheory(int value)
            {
                _ = value;
            }

            [Fact]
            [Proves("TF-033 AC2")]
            public void Tagged()
            {
            }

            [Fact]
            [Proves("TF-033", "ADR-0029")]
            [Proves("REQ-012 AC1")]
            public void TaggedSeveralWays()
            {
            }

            [Fact]
            [Proves("tf-033 ac2")]
            public void Lowercase()
            {
            }

            [Fact]
            [Proves("TF-033 AC0", "ADR-29")]
            public void OutOfForm()
            {
            }

            [Fact]
            [Untraced("draws nothing a document states")]
            public void Unproven()
            {
            }

            [Fact]
            [Untraced("both")]
            [Proves("TF-033")]
            public void UntracedAndProves()
            {
            }

            public void NotATest()
            {
            }
        }
        """;

    [Fact]
    [Proves("ADR-0044")]
    public async Task TheBuildRefusesATestMethodThatDoesNotNameWhatItProves()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "proves-fixtures", Ids.New().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var testing = Path.Combine(Repository.Root, "tests", "Iron.Testing", "Iron.Testing.csproj");
            var project = Path.Combine(folder, "Fixture.csproj");
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(project, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <IronLayer>Test</IronLayer>
                    <OutputType>Exe</OutputType>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{testing}" />
                  </ItemGroup>
                </Project>
                """, ct);
            await File.WriteAllTextAsync(Path.Combine(folder, "ProbeTests.cs"), Probe, ct);
            await File.WriteAllTextAsync(Path.Combine(folder, "Program.cs"), "return 0;\n", ct);

            var (exitCode, output) = await DotnetCli.RunAsync(["build", project, "--configuration", DotnetCli.Configuration, "-nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-v:q"]);

            Assert.True(exitCode != 0, $"The build accepted test methods without what they prove:{Environment.NewLine}{output}");
            Assert.Contains("error IRON0005: Test method 'Untagged' does not name what it proves", output, StringComparison.Ordinal);
            Assert.Contains("error IRON0005: Test method 'UntaggedTheory' does not name what it proves", output, StringComparison.Ordinal);
            Assert.Contains("error IRON0006: 'tf-033 ac2' is not a requirement criterion", output, StringComparison.Ordinal);
            Assert.Contains("error IRON0006: 'TF-033 AC0' is not a requirement criterion", output, StringComparison.Ordinal);
            Assert.Contains("error IRON0006: 'ADR-29' is not a requirement criterion", output, StringComparison.Ordinal);
            Assert.Contains("error IRON0007: Test method 'UntracedAndProves' is both [Untraced] and [Proves]", output, StringComparison.Ordinal);
            foreach (var accepted in new[] { "'Tagged'", "'TaggedSeveralWays'", "'Unproven'", "'NotATest'", "TF-033 AC2'", "REQ-012 AC1'" })
            {
                Assert.DoesNotContain(accepted, output, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
