using System.Text.RegularExpressions;

namespace Iron.Kernel;

/// <summary>How much damage a wrongly granted permission can do. Shown in the role editor; High and Critical grants are audited with extra detail (S2).</summary>
public enum Risk
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>
/// One thing a principal may do, declared in code as <c>module.resource.action</c> (TF-030): <c>identity.users.read</c>.
/// Modules declare their permissions as static fields, list them in their <c>IModule</c>, and put them on endpoints with
/// <c>RequirePermission</c>. Two permissions are equal when their names are equal.
/// </summary>
public sealed partial class Permission : IEquatable<Permission>
{
    public Permission(string name, string description, Risk risk)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a permission name. Use module.resource.action in lowercase, for example identity.users.read.", nameof(name));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Name = name;
        Description = description;
        Risk = risk;
    }

    public string Name { get; }

    public string Description { get; }

    public Risk Risk { get; }

    /// <summary>The first segment: the module that owns the permission.</summary>
    public string Module => Name[..Name.IndexOf('.', StringComparison.Ordinal)];

    public string Resource => Name.Split('.')[1];

    public string Action => Name[(Name.LastIndexOf('.') + 1)..];

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public bool Equals(Permission? other) => other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as Permission);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    public override string ToString() => Name;

    [GeneratedRegex("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
