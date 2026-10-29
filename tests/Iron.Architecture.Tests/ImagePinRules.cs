using System.Text.RegularExpressions;

namespace Iron.Architecture.Tests;

/// <summary>
/// Every image an image is built from, and every third-party image of the laptop's stack, is pinned by its version and
/// its digest, and Dependabot keeps it current (ADR-0055, since V0): a version tag alone is rebuilt under the same name.
/// A Dockerfile's <c>FROM</c> and <c>COPY --from</c> may name an earlier stage instead; a compose file's image built here
/// (named through a variable, as <c>${IMAGE_REPOSITORY}/iron-api:${IMAGE_TAG}</c>) is the build's own.
/// </summary>
public static partial class ImagePinRules
{
    private static readonly string[] Skipped = ["bin", "obj", "node_modules", ".git", "artifacts", "test-results", "TestResults"];

    /// <summary>What a run found, so a test can tell a clean repository from a search that lost its way.</summary>
    public sealed record Scan(IReadOnlyList<string> Problems, int Images);

    public static Scan Check(string root)
    {
        var problems = new List<string>();
        var images = 0;
        var dockerDirectories = new SortedSet<string>(StringComparer.Ordinal);
        var composeDirectories = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Files(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var directory = "/" + (Path.GetDirectoryName(relative) ?? string.Empty).Replace('\\', '/');
            var text = File.ReadAllText(file);
            var (found, references) = IsDockerfile(file) ? Dockerfile(relative, text) : Compose(relative, text);
            problems.AddRange(found);
            images += references;
            if (references > 0)
            {
                (IsDockerfile(file) ? dockerDirectories : composeDirectories).Add(directory);
            }
        }

        var dependabot = Path.Combine(root, ".github", "dependabot.yml");
        var covered = File.Exists(dependabot) ? Dependabot(File.ReadAllText(dependabot)) : [];
        problems.AddRange(dockerDirectories.Where(d => !covered.Contains(("docker", d))).Select(d => $"{d}: its Dockerfile's images are in no Dependabot docker entry"));
        problems.AddRange(composeDirectories.Where(d => !covered.Contains(("docker-compose", d))).Select(d => $"{d}: its compose file's images are in no Dependabot docker-compose entry"));
        return new Scan(problems, images);
    }

    /// <summary>A reference with its version and its digest: <c>name:version@sha256:</c> and 64 hexadecimal digits.</summary>
    public static bool IsPinned(string reference) => Pinned().IsMatch(reference);

    private static (List<string> Problems, int References) Dockerfile(string relative, string text)
    {
        var problems = new List<string>();
        var stages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = 0;
        var lines = text.Split('\n');
        for (var number = 1; number <= lines.Length; number++)
        {
            var line = lines[number - 1].Trim();
            if (From().Match(line) is { Success: true } from)
            {
                var image = from.Groups["image"].Value;
                if (!stages.Contains(image))
                {
                    references++;
                    Check(image, number);
                }

                if (from.Groups["stage"].Success)
                {
                    stages.Add(from.Groups["stage"].Value);
                }
            }

            foreach (Match copy in CopyFrom().Matches(line))
            {
                var source = copy.Groups["source"].Value;
                if (!stages.Contains(source) && !source.All(char.IsAsciiDigit))
                {
                    references++;
                    Check(source, number);
                }
            }
        }

        return (problems, references);

        void Check(string image, int number)
        {
            if (!IsPinned(image))
            {
                problems.Add($"{relative}:{number}: {image} is not pinned by its version and its digest");
            }
        }
    }

    private static (List<string> Problems, int References) Compose(string relative, string text)
    {
        var problems = new List<string>();
        var references = 0;
        var lines = text.Split('\n');
        for (var number = 1; number <= lines.Length; number++)
        {
            if (ComposeImage().Match(lines[number - 1]) is not { Success: true } match || match.Groups["image"].Value.Contains("${", StringComparison.Ordinal))
            {
                continue;
            }

            references++;
            var image = match.Groups["image"].Value.Trim('"', '\'');
            if (!IsPinned(image))
            {
                problems.Add($"{relative}:{number}: {image} is not pinned by its version and its digest");
            }
        }

        return (problems, references);
    }

    /// <summary>The (ecosystem, directory) pairs Dependabot's entries name, from its file's own simple layout.</summary>
    private static HashSet<(string Ecosystem, string Directory)> Dependabot(string text)
    {
        var covered = new HashSet<(string, string)>();
        string? ecosystem = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("- package-ecosystem:", StringComparison.Ordinal))
            {
                ecosystem = line["- package-ecosystem:".Length..].Trim();
            }
            else if (ecosystem is not null && line.StartsWith("directory:", StringComparison.Ordinal))
            {
                covered.Add((ecosystem, line["directory:".Length..].Trim()));
            }
            else if (ecosystem is not null && line.StartsWith("- /", StringComparison.Ordinal))
            {
                covered.Add((ecosystem, line[2..].Trim()));
            }
        }

        return covered;
    }

    private static bool IsDockerfile(string file) => Path.GetFileName(file) is var name && (name == "Dockerfile" || name.EndsWith(".Dockerfile", StringComparison.Ordinal));

    private static bool IsCompose(string file) => Path.GetFileName(file) is var name && (name is "compose.yaml" or "compose.yml" || name.EndsWith(".compose.yaml", StringComparison.Ordinal) || name.StartsWith("docker-compose", StringComparison.Ordinal));

    private static List<string> Files(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory).Where(d => !Skipped.Contains(Path.GetFileName(d), StringComparer.Ordinal)))
            {
                pending.Push(child);
            }

            files.AddRange(Directory.EnumerateFiles(directory).Where(f => IsDockerfile(f) || IsCompose(f)));
        }

        // In the repository's order, whatever order the file system lists them in.
        return [.. files.Order(StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._/-]*:[A-Za-z0-9_][A-Za-z0-9._-]*@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pinned();

    [GeneratedRegex(@"^FROM\s+(?:--\S+\s+)*(?<image>\S+)(?:\s+AS\s+(?<stage>\S+))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex From();

    [GeneratedRegex(@"--from=(?<source>[^\s,]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CopyFrom();

    [GeneratedRegex(@"^\s*image:\s*(?<image>[^\s#]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ComposeImage();
}
