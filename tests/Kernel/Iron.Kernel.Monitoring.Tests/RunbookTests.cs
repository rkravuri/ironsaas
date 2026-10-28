using System.Text.RegularExpressions;
using Iron.Kernel.Monitoring.Services;

namespace Iron.Kernel.Monitoring.Tests;

/// <summary>
/// Every alert leads to its runbook (TF-209 AC3, AC4, ADR-0056): each rule names a section of the incident runbook, which
/// must exist in the same commit, so an alert merged without its runbook fails the tests and its pull request.
/// </summary>
public sealed partial class RunbookTests
{
    [Fact]
    [Proves("TF-209 AC3", "TF-209 AC4", "ADR-0056")]
    public void EveryRuleAndAFailingRuleNameARunbookSectionThatExists()
    {
        var runbook = File.ReadAllText(Path.Combine(Root(), "docs", "security", "incident-response.md"));
        var anchors = runbook.Split('\n')
            .Select(line => Heading().Match(line.TrimEnd('\r')))
            .Where(match => match.Success)
            .Select(match => Anchor(match.Groups["title"].Value))
            .ToHashSet(StringComparer.Ordinal);

        var missing = AlertRules.All.Select(rule => (rule.Name, rule.Runbook))
            .Append(("a failing rule", AlertRules.FailingRunbook))
            .Where(pair => !pair.Item2.StartsWith(AlertRules.RunbookPath + "#", StringComparison.Ordinal) || !anchors.Contains(pair.Item2[(pair.Item2.IndexOf('#', StringComparison.Ordinal) + 1)..]))
            .Select(pair => $"{pair.Item1}: {pair.Item2}")
            .ToList();

        Assert.True(missing.Count == 0, $"Rules whose runbook section is missing from {AlertRules.RunbookPath}: {string.Join("; ", missing)}");
        Assert.Contains("the-72-hour-notice", anchors);
    }

    /// <summary>The anchor GitHub gives a heading: lower case, punctuation but hyphens dropped, spaces as hyphens.</summary>
    private static string Anchor(string title) => Punctuation().Replace(title.Trim().ToLowerInvariant(), string.Empty).Replace(' ', '-');

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Foundation.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [GeneratedRegex(@"^#{2,4}\s+(?<title>.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"[^\p{L}\p{N}\- ]")]
    private static partial Regex Punctuation();
}
