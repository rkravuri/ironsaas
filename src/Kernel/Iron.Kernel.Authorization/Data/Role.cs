using System.Text.RegularExpressions;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Authorization.Data;

/// <summary>Who a role is meant for (TF-032): people or service accounts. Only the tenant administrator system role is for both (ADR-0031).</summary>
public enum PrincipalType
{
    User,
    Service,
    Any,
}

/// <summary>
/// A named set of permissions (TF-032). The code identifies the role in assignments, export and import; the name is for
/// people (ADR-0031). System roles are seeded and cannot be edited; tenant roles belong to the administrators.
/// </summary>
public sealed partial class Role : AuditedEntity
{
    public const int CodeMaxLength = 100;
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 500;

    private Role()
    {
        Code = string.Empty;
        Name = string.Empty;
        Description = string.Empty;
        Permissions = [];
    }

    public Role(string code, string name, string description, bool isSystem, PrincipalType principalType, IEnumerable<string> permissions)
    {
        if (!IsValidCode(code))
        {
            throw new ArgumentException($"'{code}' is not a role code. Use lowercase letters, digits and hyphens, 2 to {CodeMaxLength} characters.", nameof(code));
        }

        Code = code;
        Name = RequireName(name);
        Description = RequireDescription(description);
        IsSystem = isSystem;
        PrincipalType = principalType;
        Permissions = Normalise(permissions);
    }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public string Description { get; private set; }

    public bool IsSystem { get; private set; }

    public PrincipalType PrincipalType { get; private set; }

    /// <summary>Permission names, distinct and sorted. Stored as a PostgreSQL text array.</summary>
    public List<string> Permissions { get; private set; }

    public static bool IsValidCode(string? code) => code is not null && code.Length <= CodeMaxLength && CodePattern().IsMatch(code);

    public static bool IsValidName(string? name) => name is not null && name.Trim().Length is > 0 and <= NameMaxLength;

    /// <summary>Whether a principal of this kind may hold the role: the role's type, or any type for a role meant for both.</summary>
    public bool IsFor(PrincipalType principal) => PrincipalType == PrincipalType.Any || PrincipalType == principal;

    public void Change(string name, string description, PrincipalType principalType, IEnumerable<string> permissions)
    {
        Name = RequireName(name);
        Description = RequireDescription(description);
        PrincipalType = principalType;
        Permissions = Normalise(permissions);
    }

    public void ReplacePermissions(IEnumerable<string> permissions) => Permissions = Normalise(permissions);

    public static List<string> Normalise(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return permissions.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private static string RequireName(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"A role needs a name of 1 to {NameMaxLength} characters.", nameof(name));
        }

        return name.Trim();
    }

    private static string RequireDescription(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        var trimmed = description.Trim();
        if (trimmed.Length > DescriptionMaxLength)
        {
            throw new ArgumentException($"A role's description has at most {DescriptionMaxLength} characters.", nameof(description));
        }

        return trimmed;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
