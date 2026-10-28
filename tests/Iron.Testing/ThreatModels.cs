using System.Text.RegularExpressions;

namespace Iron.Testing;

/// <summary>
/// One threat model record of <c>docs/security/threat-models</c>, as its heading lines name it: a line that is missing
/// is null, and a line that says "none" an empty list.
/// </summary>
public sealed record ThreatModelRecord(string File, IReadOnlyList<string>? Modules, IReadOnlyList<string>? Endpoints, string? Reviewed, IReadOnlyList<string> Categories);

/// <summary>
/// The threat model check (TF-077 AC2, ADR-0055): every module, by the name its <c>IModule</c> declares, and every
/// anonymous endpoint, as the host describes it (<c>POST /auth/login</c>), is named by a record in
/// <c>docs/security/threat-models</c>, and every record is in the template's form: its modules, its endpoints, the date
/// it was reviewed, and a threat for each of the six categories. A record names only what exists, so it cannot go stale
/// unseen. The architecture tests read the modules and the host tests the endpoints; both read the records here.
/// </summary>
public static partial class ThreatModels
{
    public static readonly IReadOnlyList<string> Categories = ["Spoofing", "Tampering", "Repudiation", "Information disclosure", "Denial of service", "Elevation of privilege"];

    private static readonly string[] NotRecords = ["README.md", "0000-template.md"];

    /// <summary>The records' folder of the repository that holds <paramref name="start"/>.</summary>
    public static string Folder(string? start = null)
    {
        var directory = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Foundation.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Foundation.slnx was not found above the test's folder."), "docs", "security", "threat-models");
    }

    public static IReadOnlyList<ThreatModelRecord> Read(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*.md")
            .Where(path => !NotRecords.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(path => Parse(Path.GetFileName(path), File.ReadAllText(path)))];

    public static ThreatModelRecord Parse(string file, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var categories = lines
            .Select(line => TableRow().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups["first"].Value.Trim())
            .Where(cell => Categories.Contains(cell, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new ThreatModelRecord(file, Items(Field(lines, "Modules")), Items(Field(lines, "Endpoints")), Field(lines, "Reviewed"), categories);
    }

    /// <summary>The records' own faults: a heading line missing, or a category with no threat.</summary>
    public static IReadOnlyList<string> FormProblems(IReadOnlyList<ThreatModelRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var problems = new List<string>();
        foreach (var record in records)
        {
            if (record.Modules is null || record.Endpoints is null || string.IsNullOrWhiteSpace(record.Reviewed))
            {
                problems.Add($"{record.File} lacks a line of the template's heading (- **Modules:**, - **Endpoints:**, - **Reviewed:**): copy 0000-template.md (ADR-0055)");
            }

            problems.AddRange(Categories.Except(record.Categories, StringComparer.Ordinal).Select(category => $"{record.File} has no threat of the category '{category}' (ADR-0055)"));
        }

        return problems;
    }

    /// <summary>Each name the records must hold and do not, and each name a record holds that is not there.</summary>
    public static IReadOnlyList<string> Coverage(IReadOnlyList<ThreatModelRecord> records, string what, IEnumerable<string> names, Func<ThreatModelRecord, IReadOnlyList<string>?> named)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(named);
        var expected = names.ToHashSet(StringComparer.Ordinal);
        var covered = records.SelectMany(record => (named(record) ?? []).Select(name => (Name: name, record.File))).ToList();
        var missing = expected.Except(covered.Select(c => c.Name), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name => $"the {what} '{name}' has no threat model record in docs/security/threat-models: write one from 0000-template.md before it is built (TF-077 AC2)");
        var unknown = covered.Where(c => !expected.Contains(c.Name))
            .Select(c => $"{c.File} names the {what} '{c.Name}', which does not exist: correct the record (ADR-0055)");
        return [.. missing, .. unknown];
    }

    private static string? Field(List<string> lines, string name) =>
        lines.Select(line => Heading().Match(line)).FirstOrDefault(match => match.Success && match.Groups["name"].Value == name)?.Groups["value"].Value.Trim();

    private static IReadOnlyList<string>? Items(string? value) =>
        value is null
            ? null
            : string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
                ? []
                : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(item => item.Trim('`').Trim())];

    [GeneratedRegex(@"^- \*\*(?<name>Modules|Endpoints|Reviewed):\*\*\s*(?<value>.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\|(?<first>[^|]+)\|")]
    private static partial Regex TableRow();
}
