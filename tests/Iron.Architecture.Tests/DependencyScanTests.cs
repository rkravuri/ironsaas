using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;

namespace Iron.Architecture.Tests;

/// <summary>
/// The dependency scan with its gate, end to end (TF-076 AC1, AC2, ADR-0055): the Trivy image make security pins reads a
/// lock file that holds lodash 4.17.11, whose prototype pollution (CVE-2019-10744) is critical, and tools/security_gate.py
/// fails naming the package, its version and the finding; an unexpired exception lets it through, and the same
/// exception after its expiry fails the build again, naming both. A folder with no dependency passes.
/// </summary>
public partial class DependencyScanTests
{
    private const string Vulnerable = "CVE-2019-10744";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-076 AC1", "TF-076 AC2", "ADR-0055")]
    public async Task ACriticalDependencyFailsNamingThePackageAndAnExceptionHoldsOnlyUntilItExpires()
    {
        var dirty = Folder();
        var clean = Folder();
        await File.WriteAllTextAsync(Path.Combine(dirty, "package-lock.json"), Lock(("lodash", "4.17.11")), Cancel);
        await File.WriteAllTextAsync(Path.Combine(clean, "package-lock.json"), Lock(), Cancel);
        await ScanAsync(dirty);
        await ScanAsync(clean);

        // Every critical and high finding the scanner reports for the package is excepted: lodash 4.17.11 has several,
        // and the advisories grow, so the list comes from the scan itself.
        var serious = await SeriousFindingsAsync(dirty);
        Assert.Contains(Vulnerable, serious);
        var none = Exceptions(dirty, "none.yaml", "exceptions: []\n");
        var excepted = Exceptions(dirty, "excepted.yaml", "exceptions:\n" + string.Concat(serious.Select(finding => $"""
              - finding: {finding}
                package: lodash
                reason: The fixture's package, waiting for its upgrade.
                approver: The test
                approved: 2031-01-10
                expires: 2031-03-10

            """)));

        var failed = await GateAsync(dirty, none, "2031-02-01");
        var allowed = await GateAsync(dirty, excepted, "2031-02-01");
        var expired = await GateAsync(dirty, excepted, "2031-03-10");
        var passed = await GateAsync(clean, none, "2031-02-01");

        Assert.True(failed.Exit == 1, failed.Output);
        Assert.Contains($"FAIL critical {Vulnerable} in lodash 4.17.11", failed.Output, StringComparison.Ordinal);
        Assert.True(allowed.Exit == 0, allowed.Output);
        Assert.Contains($"excepted until 2031-03-10: critical {Vulnerable} in lodash 4.17.11", allowed.Output, StringComparison.Ordinal);
        Assert.True(expired.Exit == 1, expired.Output);
        Assert.Contains($"({Vulnerable} in lodash): expired on 2031-03-10", expired.Output, StringComparison.Ordinal);
        Assert.Contains($"FAIL critical {Vulnerable} in lodash 4.17.11", expired.Output, StringComparison.Ordinal);
        Assert.True(passed.Exit == 0, passed.Output);
    }

    private static async Task<IReadOnlyList<string>> SeriousFindingsAsync(string folder)
    {
        using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "trivy.json"), Cancel));
        return [.. report.RootElement.GetProperty("Results").EnumerateArray()
            .SelectMany(result => result.TryGetProperty("Vulnerabilities", out var found) ? found.EnumerateArray() : Enumerable.Empty<System.Text.Json.JsonElement>())
            .Where(v => v.GetProperty("Severity").GetString() is "CRITICAL" or "HIGH")
            .Select(v => v.GetProperty("VulnerabilityID").GetString() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)];
    }

    private static string Lock(params (string Name, string Version)[] packages)
    {
        var dependencies = string.Join(", ", packages.Select(p => $"\"{p.Name}\": \"{p.Version}\""));
        var entries = string.Concat(packages.Select(p => $", \"node_modules/{p.Name}\": {{ \"version\": \"{p.Version}\", \"license\": \"MIT\" }}"));
        return $$"""{ "name": "fixture", "version": "1.0.0", "lockfileVersion": 3, "requires": true, "packages": { "": { "name": "fixture", "version": "1.0.0", "dependencies": { {{dependencies}} } }{{entries}} } }""";
    }

    private static string Exceptions(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>The scan as make security runs it, at the image the Makefile pins, with the laptop's database cache when there is one.</summary>
    private static async Task ScanAsync(string folder)
    {
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "trivy");
        Directory.CreateDirectory(cache);
        await using var scanner = new ContainerBuilder(PinnedImage())
            .WithBindMount(folder, "/scan")
            .WithBindMount(cache, "/root/.cache/trivy")
            .WithCommand("fs", "--quiet", "--scanners", "vuln", "--format", "json", "--output", "/scan/trivy.json", "/scan")
            .Build();
        await scanner.StartAsync(Cancel);
        Assert.Equal(0, await scanner.GetExitCodeAsync(Cancel));
    }

    private static async Task<(int Exit, string Output)> GateAsync(string folder, string exceptions, string today)
    {
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { Path.Combine(Root(), "tools", "security_gate.py"), "--trivy", Path.Combine(folder, "trivy.json"), "--exceptions", exceptions, "--licences", Path.Combine(Root(), "security", "licences.yaml"), "--today", today })
        {
            start.ArgumentList.Add(argument);
        }

        using var gate = Process.Start(start) ?? throw new InvalidOperationException("python3 did not start.");
        var output = await gate.StandardOutput.ReadToEndAsync(Cancel) + await gate.StandardError.ReadToEndAsync(Cancel);
        await gate.WaitForExitAsync(Cancel);
        return (gate.ExitCode, output);
    }

    private static string PinnedImage()
    {
        var makefile = File.ReadAllText(Path.Combine(Root(), "Makefile"));
        return Pin().Match(makefile) is { Success: true } match ? match.Groups["image"].Value : throw new InvalidOperationException("The Makefile pins no TRIVY_IMAGE.");
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Foundation.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"dependency-scan-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}");
        Directory.CreateDirectory(folder);

        // The scanner runs as root in its container and writes its report here; the tests run on Linux, where Docker is.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }

        return folder;
    }

    [GeneratedRegex(@"^TRIVY_IMAGE := (?<image>\S+)$", RegexOptions.Multiline)]
    private static partial Regex Pin();
}
