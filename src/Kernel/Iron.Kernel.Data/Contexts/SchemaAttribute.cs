using System.Reflection;

namespace Iron.Kernel.Data.Contexts;

/// <summary>
/// Names the PostgreSQL schema of a module's DbContext: <c>[Schema("kernel_identity")]</c>. One module, one schema,
/// named after the module with its layer's prefix: kernel_, business_ or products_ (TF-080, ADR-0029). Lowercase letters, digits and underscores, starting with a letter.
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

    /// <summary>The prefixes a module schema starts with, one per layer (ADR-0029).</summary>
    public static IReadOnlyList<string> LayerPrefixes { get; } = ["kernel_", "business_", "products_"];

    /// <summary>True when the name starts with a layer prefix and names a module after it: <c>kernel_identity</c>, not <c>identity</c>.</summary>
    public static bool HasLayerPrefix(string? name) =>
        name is not null && LayerPrefixes.Any(prefix => name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal));

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
