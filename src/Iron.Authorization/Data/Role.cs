using System.Text.RegularExpressions;
using Iron.Data.Entities;

namespace Iron.Authorization.Data;

/// <summary>Who a role is meant for (TF-032): people, service accounts, or both.</summary>
public enum PrincipalType
{
    User,
    Service,
    Any,
}

/// <summary>A named set of permissions (TF-032). System roles are seeded and cannot be edited; tenant roles belong to the administrators.</summary>
public sealed partial class Role : AuditedEntity
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;

    private Role()
    {
        Name = string.Empty;
        Description = string.Empty;
        Permissions = [];
    }

    public Role(string name, string description, bool isSystem, PrincipalType principalType, IEnumerable<string> permissions)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a role name. Use lowercase letters, digits and hyphens, 2 to {NameMaxLength} characters.", nameof(name));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(permissions);
        Name = name;
        Description = description;
        IsSystem = isSystem;
        PrincipalType = principalType;
        Permissions = Normalise(permissions);
    }

    public string Name { get; private set; }

    public string Description { get; private set; }

    public bool IsSystem { get; private set; }

    public PrincipalType PrincipalType { get; private set; }

    /// <summary>Permission names, distinct and sorted. Stored as a PostgreSQL text array.</summary>
    public List<string> Permissions { get; private set; }

    public static bool IsValidName(string? name) => name is not null && name.Length <= NameMaxLength && NamePattern().IsMatch(name);

    public void ReplacePermissions(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        Permissions = Normalise(permissions);
    }

    private static List<string> Normalise(IEnumerable<string> permissions) =>
        permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    [GeneratedRegex("^[a-z][a-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
