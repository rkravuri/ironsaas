namespace Iron.Architecture.Tests;

/// <summary>Every tracked shell script keeps its executable bit, and the check that holds it (V2's review).</summary>
public class ScriptModeRulesTests
{
    private const string Object = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    [Untraced("A repository rule from V2's review, after editing through the Windows path dropped a script's executable bit twice; no requirement or ADR states file modes.")]
    public async Task EveryTrackedShellScriptIsExecutable()
    {
        var scan = await ScriptModeRules.CheckAsync(Repository.Root);

        // Twenty scripts under infra/compose/scripts, tests and tools, on 2026-10-31.
        Assert.True(scan.Scripts >= 20, $"Only {scan.Scripts} shell scripts were found in git's index; the search lost its way.");
        ArchitectureAssert.NoViolations($"Shell scripts executable ({scan.Scripts} scripts)", scan.Problems);
    }

    [Fact]
    [Untraced("A repository rule from V2's review, after editing through the Windows path dropped a script's executable bit twice; no requirement or ADR states file modes.")]
    public void AScriptWithoutItsExecutableBitFailsNamingItAndTheFix()
    {
        var index = string.Concat(
            $"100755 {Object} 0\tinfra/compose/scripts/provision.sh\0",
            $"100644 {Object} 0\tinfra/compose/scripts/ci-integration.sh\0",
            $"100644 {Object} 0\tsrc/Program.cs\0",
            $"120000 {Object} 0\ttools/linked script.sh\0",
            $"100644 {Object} 0\tdocs/shell.sh.md\0");

        var scan = ScriptModeRules.Check(index);

        Assert.Equal(3, scan.Scripts);
        Assert.Equal(
        [
            "infra/compose/scripts/ci-integration.sh: mode 100644, not 100755; run 'chmod +x infra/compose/scripts/ci-integration.sh' in WSL (or 'git update-index --chmod=+x infra/compose/scripts/ci-integration.sh') and commit it",
            "tools/linked script.sh: mode 120000, not 100755; run 'chmod +x tools/linked script.sh' in WSL (or 'git update-index --chmod=+x tools/linked script.sh') and commit it",
        ], scan.Problems);
    }
}
