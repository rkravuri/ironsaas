using System.Diagnostics;

namespace Iron.Architecture.Tests;

/// <summary>
/// The build's own layer check (target IronCheckLayer in Directory.Build.props). A fixture project written next to this
/// test's assembly inherits the repository's build settings, as every project does; the target must stop it without a
/// valid layer and let it through with one. Build output folders are never read as projects (ProjectCatalog).
/// </summary>
public class LayerBuildCheckTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData(null, "IRON0001")]
    [InlineData("Kernal", "IRON0002")]
    [InlineData("kernel", "IRON0002")]
    [InlineData("Kernel", null)]
    public async Task TheBuildStopsAProjectWithoutAValidLayer(string? layer, string? expectedError)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "layer-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var property = layer is null ? string.Empty : $"<PropertyGroup><IronLayer>{layer}</IronLayer></PropertyGroup>";
            var project = Path.Combine(folder, "Fixture.csproj");
            await File.WriteAllTextAsync(project, $"<Project Sdk=\"Microsoft.NET.Sdk\">{property}</Project>", TestContext.Current.CancellationToken);

            var (exitCode, output) = await RunDotnetAsync(["msbuild", project, "-t:IronCheckLayer", "-nologo", "-nodeReuse:false", "-v:q"]);

            if (expectedError is null)
            {
                Assert.True(exitCode == 0, output);
            }
            else
            {
                Assert.True(exitCode != 0, $"The build accepted IronLayer '{layer}'.");
                Assert.Contains($"error {expectedError}", output, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(DotnetPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Limit);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await standardOutput + await standardError);
    }

    /// <summary>The dotnet that runs this test: the CLI's own path when it says so, else the root above the shared runtime.</summary>
    private static string DotnetPath()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host) && File.Exists(host))
        {
            return host;
        }

        // The runtime lives in <root>/shared/Microsoft.NETCore.App/<version>/; the dotnet executable sits in <root>.
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        return Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }
}
