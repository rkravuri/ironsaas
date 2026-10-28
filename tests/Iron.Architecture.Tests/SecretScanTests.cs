using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;

namespace Iron.Architecture.Tests;

/// <summary>
/// The repository's secret scan (TF-073 AC1, ADR-0054): the gitleaks CI runs, at the version CI pins, refuses a folder
/// with a secret in a file, naming the file and the secret's type, and passes a clean one. The secret is a private key
/// made at run time, so no commit of this repository holds one for the scan to find.
/// </summary>
public partial class SecretScanTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-073 AC1", "ADR-0054")]
    public async Task ASecretInAFileFailsTheScanNamingTheFileAndTheSecretTypeAndACleanFolderPasses()
    {
        var image = $"ghcr.io/gitleaks/gitleaks:v{PinnedVersion()}";
        var dirty = Folder();
        var clean = Folder();
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            Directory.CreateDirectory(Path.Combine(dirty, "config"));
            await File.WriteAllTextAsync(Path.Combine(dirty, "config", "appsettings.Production.json"), JsonSerializer.Serialize(new { Tokens = new { SigningKeyPem = key.ExportECPrivateKeyPem() } }, new JsonSerializerOptions { WriteIndented = true }), Cancel);
        }

        await File.WriteAllTextAsync(Path.Combine(dirty, "README.md"), "Nothing to see here.", Cancel);
        await File.WriteAllTextAsync(Path.Combine(clean, "README.md"), "Nothing to see here.", Cancel);

        var (dirtyExit, findings) = await ScanAsync(image, dirty);
        var (cleanExit, none) = await ScanAsync(image, clean);

        Assert.Equal(1, dirtyExit);
        Assert.NotEmpty(findings);
        Assert.All(findings, finding => Assert.Equal(("private-key", "/scan/config/appsettings.Production.json"), (finding.GetProperty("RuleID").GetString(), finding.GetProperty("File").GetString())));
        Assert.Equal(0, cleanExit);
        Assert.Empty(none);
    }

    /// <summary>The version CI's secret scan pins, read from its workflow, so the test and the pipeline run the same rules.</summary>
    private static string PinnedVersion()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Foundation.slnx")))
        {
            directory = directory.Parent;
        }

        var workflow = File.ReadAllText(Path.Combine(directory?.FullName ?? throw new InvalidOperationException("The repository root was not found."), ".github", "workflows", "ci.yml"));
        return Pin().Match(workflow) is { Success: true } match ? match.Groups["version"].Value : throw new InvalidOperationException("CI's gitleaks step pins no GITLEAKS_VERSION.");
    }

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"secret-scan-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}");
        Directory.CreateDirectory(folder);

        // The scanner's user writes its report here; the tests run on Linux, where Docker is.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }

        return folder;
    }

    private static async Task<(long Exit, IReadOnlyList<JsonElement> Findings)> ScanAsync(string image, string folder)
    {
        var report = Path.Combine(folder, "report.json");
        await using var scanner = new ContainerBuilder(image)
            .WithBindMount(folder, "/scan")
            .WithCommand("dir", "/scan", "--no-banner", "--redact", "--report-format", "json", "--report-path", "/scan/report.json", "--exit-code", "1")
            .Build();
        await scanner.StartAsync(Cancel);
        var exit = await scanner.GetExitCodeAsync(Cancel);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report, Cancel));
        IReadOnlyList<JsonElement> findings = [.. document.RootElement.EnumerateArray().Select(f => f.Clone()).Where(f => f.GetProperty("File").GetString() != "/scan/report.json")];
        return (exit, findings);
    }

    [GeneratedRegex(@"GITLEAKS_VERSION:\s*""?(?<version>\d+\.\d+\.\d+)""?")]
    private static partial Regex Pin();
}
