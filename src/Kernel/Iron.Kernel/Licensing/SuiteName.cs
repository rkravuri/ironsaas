using System.Reflection;
using System.Text.RegularExpressions;

namespace Iron.Kernel;

/// <summary>
/// The names of suites (ADR-0043). A suite project declares its suite in PascalCase, <c>&lt;IronSuite&gt;Procurement&lt;/IronSuite&gt;</c>;
/// its short name is the same word in lowercase, <c>procurement</c>, which its schemas, its shell folder and the
/// catalogue use. The kernel and business layers are outside every suite: the Foundation, named <c>foundation</c>.
/// </summary>
public static partial class SuiteName
{
    /// <summary>The catalogue's name for the kernel and business layers: what every tenant has.</summary>
    public const string Foundation = "foundation";

    /// <summary>The assembly metadata key a suite project's build writes its IronSuite into (Directory.Build.props).</summary>
    public const string MetadataKey = "IronSuite";

    /// <summary>Two layers and the Foundation: a suite with one of these names would read as something it is not.</summary>
    private static readonly string[] Reserved = ["kernel", "business", Foundation];

    /// <summary>A suite's name as a project declares it: one word in PascalCase, not a layer and not the Foundation.</summary>
    public static bool IsValid(string? name) =>
        name is not null && name.Length <= 40 && DeclaredPattern().IsMatch(name) && !Reserved.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>A suite's short name: the declared name in lowercase, as <c>procurement</c>; never <c>foundation</c>.</summary>
    public static bool IsShortName(string? name) =>
        name is not null && name.Length <= 40 && ShortPattern().IsMatch(name) && !Reserved.Contains(name, StringComparer.Ordinal);

    /// <summary>The short name of a declared suite name: <c>Procurement</c> is <c>procurement</c>.</summary>
    public static string ShortName(string name)
    {
        if (!IsValid(name))
        {
            throw new ArgumentException($"'{name}' is not a suite name. Use one word in PascalCase, other than Kernel, Business and Foundation (ADR-0043).", nameof(name));
        }

        return name.ToLowerInvariant();
    }

    /// <summary>
    /// The short name of the suite an assembly belongs to, from the metadata its build writes from <c>IronSuite</c>, or
    /// <see cref="Foundation"/> for an assembly outside every suite.
    /// </summary>
    public static string Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var declared = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == MetadataKey)?.Value;
        return declared is null ? Foundation : ShortName(declared);
    }

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DeclaredPattern();

    [GeneratedRegex("^[a-z][a-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortPattern();
}
