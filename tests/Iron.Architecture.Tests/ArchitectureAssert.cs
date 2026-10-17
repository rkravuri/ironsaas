namespace Iron.Architecture.Tests;

internal static class ArchitectureAssert
{
    /// <summary>Fails with every violation listed, one per line, so the build output says exactly what to fix.</summary>
    public static void NoViolations(string ruleName, IReadOnlyList<string> violations)
    {
        if (violations.Count == 0)
        {
            return;
        }

        var message = $"{ruleName}: {violations.Count} violation(s)." + Environment.NewLine
            + string.Join(Environment.NewLine, violations);
        Assert.Fail(message);
    }
}
