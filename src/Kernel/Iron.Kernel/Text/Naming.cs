namespace Iron.Kernel;

/// <summary>Name rules shared by the data layer (tables, columns) and the API (fields, resources).</summary>
public static class Naming
{
    /// <summary>Lowercase letters, digits and underscores, starting with a letter: <c>created_at</c>.</summary>
    public static bool IsSnakeCase(string? name) => Matches(name, '_');

    /// <summary>Lowercase letters, digits and hyphens, starting with a letter: <c>purchase-orders</c>.</summary>
    public static bool IsKebabCase(string? name) => Matches(name, '-');

    private static bool Matches(string? name, char separator)
    {
        if (string.IsNullOrEmpty(name) || !char.IsAsciiLetterLower(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != separator)
            {
                return false;
            }
        }

        return true;
    }
}
