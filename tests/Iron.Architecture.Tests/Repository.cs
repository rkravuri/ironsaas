namespace Iron.Architecture.Tests;

/// <summary>Finds the repository root from wherever the test assembly runs.</summary>
public static class Repository
{
    public const string SolutionFileName = "Foundation.slnx";

    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"{SolutionFileName} was not found in any folder above {AppContext.BaseDirectory}.");
    }
}
