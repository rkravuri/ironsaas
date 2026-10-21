using System.Diagnostics;

namespace Iron.Architecture.Tests;

/// <summary>Runs the dotnet that runs these tests, for the build checks that build a fixture project.</summary>
internal static class DotnetCli
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    public static async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> arguments)
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

    /// <summary>The configuration these tests were built in, read from their own folder (bin/Release/net10.0).</summary>
    public static string Configuration =>
        new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Debug";

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
