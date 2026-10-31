using System.ComponentModel;
using System.Diagnostics;

namespace Iron.Architecture.Tests;

/// <summary>
/// Every shell script the repository tracks keeps its executable bit, mode 100755 in git's index, so a script that lost
/// it fails the build before a make target, the pipeline or a container runs it. Editing a script through the Windows
/// path of the WSL file system dropped the bit twice (S3 T3 and V2), and git committed the new mode with the edit.
/// </summary>
public static class ScriptModeRules
{
    public const string Executable = "100755";

    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(1);

    /// <summary>What a run found, so a test can tell a clean repository from a search that lost its way.</summary>
    public sealed record Scan(IReadOnlyList<string> Problems, int Scripts);

    /// <summary>The repository's own index, read with <c>git ls-files --stage -z</c>.</summary>
    public static async Task<Scan> CheckAsync(string root) => Check(await IndexAsync(root));

    /// <summary>
    /// The scripts of an index listing, one entry per file, <c>mode object stage</c>, a tab, then the path, each ended by
    /// a NUL as <c>-z</c> writes it, so a path with any character reads as it is.
    /// </summary>
    public static Scan Check(string index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var problems = new List<string>();
        var scripts = 0;
        foreach (var entry in index.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = entry.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0 || !entry.EndsWith(".sh", StringComparison.Ordinal))
            {
                continue;
            }

            var path = entry[(tab + 1)..];
            scripts++;
            var mode = entry[..tab].Split(' ')[0];
            if (mode != Executable)
            {
                problems.Add($"{path}: mode {mode}, not {Executable}; run 'chmod +x {path}' in WSL (or 'git update-index --chmod=+x {path}') and commit it");
            }
        }

        return new Scan(problems, scripts);
    }

    private static async Task<string> IndexAsync(string root)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-C", root, "ls-files", "--stage", "-z" })
        {
            start.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Limit);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("git is not on the PATH: the check reads the modes from git's index.", exception);
        }

        using (process)
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0
                ? await standardOutput
                : throw new InvalidOperationException($"git ls-files failed in {root}: {await standardError}");
        }
    }
}
