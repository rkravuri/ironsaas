using System.Reflection;

namespace Iron.Kernel.Data.Contexts;

/// <summary>
/// Names the PostgreSQL schema of a module's DbContext: <c>[Schema("kernel_identity")]</c>. One module, one schema,
/// named after the module with its layer's prefix, kernel_ or business_, or for a suite module its suite's short name,
/// procurement_ (TF-080, ADR-0029, ADR-0043). Lowercase letters, digits and underscores, starting with a letter.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SchemaAttribute : Attribute
{
    public SchemaAttribute(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a valid schema name. Use lowercase letters, digits and underscores, starting with a letter.",
                nameof(name));
        }

        Name = name;
    }

    public string Name { get; }

    /// <summary>The prefixes of the Foundation's module schemas, one per layer (ADR-0029). A suite's start with its short name (ADR-0043).</summary>
    public static IReadOnlyList<string> LayerPrefixes { get; } = ["kernel_", "business_"];

    /// <summary>
    /// The prefixes a module schema may start with: the Foundation's layers, then those of these suites, given by short
    /// name (<c>procurement</c> gives <c>procurement_</c>). The kernel names no suite; the caller knows its modules'.
    /// </summary>
    public static IReadOnlyList<string> Prefixes(IEnumerable<string>? suites = null)
    {
        var named = (suites ?? []).Distinct(StringComparer.Ordinal).ToList();
        var wrong = named.FirstOrDefault(suite => !SuiteName.IsShortName(suite));
        if (wrong is not null)
        {
            throw new ArgumentException($"'{wrong}' is not a suite's short name (ADR-0043).", nameof(suites));
        }

        return [.. LayerPrefixes, .. named.Order(StringComparer.Ordinal).Select(suite => suite + "_")];
    }

    /// <summary>
    /// True when the name starts with a layer prefix, or the prefix of one of these suites, and names a module after it:
    /// <c>kernel_identity</c> or <c>procurement_orders</c>, not <c>identity</c>.
    /// </summary>
    public static bool HasLayerPrefix(string? name, IEnumerable<string>? suites = null) =>
        name is not null && Prefixes(suites).Any(prefix => name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>The schema declared on a DbContext type. Throws when the attribute is missing.</summary>
    public static string For(Type contextType)
    {
        ArgumentNullException.ThrowIfNull(contextType);
        var attribute = contextType.GetCustomAttribute<SchemaAttribute>();
        return attribute?.Name
            ?? throw new InvalidOperationException($"{contextType.Name} needs a [Schema(\"...\")] attribute that names its database schema.");
    }

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsAsciiLetterLower(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }
}
