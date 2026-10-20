using System.Text.RegularExpressions;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Authorization.Data;

/// <summary>
/// One unit of the organisation tree (TF-034, ADR-0030): a name, a code, a kind code from <see cref="IOrganisationKinds"/>,
/// its parent, and the materialised path of ids from the root, <c>/&lt;root&gt;/&lt;parent&gt;/&lt;unit&gt;/</c>. No business
/// fields (ADR-0029). Only <c>OrganisationUnitService</c> changes the path, in the transaction that changes the parent.
/// </summary>
public sealed partial class OrganisationUnit : AuditedEntity
{
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 50;
    public const int KindMaxLength = 50;
    public const int MaxDepth = 16;

    /// <summary>A slash, then 32 hex characters and a slash for the root and each of the 16 levels below it.</summary>
    public const int PathMaxLength = 1 + (33 * (MaxDepth + 1));

    private OrganisationUnit()
    {
        Name = string.Empty;
        Code = string.Empty;
        Kind = string.Empty;
        Path = string.Empty;
    }

    private OrganisationUnit(Guid? parentId, string parentPath, int depth, string name, string code, string kind)
    {
        if (depth > MaxDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, $"A unit sits at most {MaxDepth} levels below the root.");
        }

        Name = RequireName(name);
        Code = RequireCode(code);
        Kind = RequireKind(kind);
        ParentId = parentId;
        Depth = depth;
        Path = parentPath + Segment(Id) + "/";
        Status = UnitStatus.Active;
    }

    public string Name { get; private set; }

    /// <summary>Lowercase letters, digits and hyphens; unique in the tenant.</summary>
    public string Code { get; private set; }

    public string Kind { get; private set; }

    /// <summary>Null for the root only.</summary>
    public Guid? ParentId { get; private set; }

    public string Path { get; private set; }

    /// <summary>0 for the root.</summary>
    public int Depth { get; private set; }

    public UnitStatus Status { get; private set; }

    /// <summary>The unit this one was merged into (TF-223, ADR-0038); a merged unit is inactive and stays so.</summary>
    public Guid? MergedIntoId { get; private set; }

    public bool IsRoot => ParentId is null;

    public bool IsActive => Status == UnitStatus.Active;

    public static OrganisationUnit Root(string name, string code, string kind) => new(null, "/", 0, name, code, kind);

    public static OrganisationUnit ChildOf(OrganisationUnit parent, string name, string code, string kind)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return new OrganisationUnit(parent.Id, parent.Path, parent.Depth + 1, name, code, kind);
    }

    /// <summary>True when <paramref name="other"/> is this unit or one of its descendants.</summary>
    public bool Contains(OrganisationUnit other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.Path.StartsWith(Path, StringComparison.Ordinal);
    }

    /// <summary>The ids on the path, root first and this unit last.</summary>
    public IReadOnlyList<Guid> AncestorsAndSelf() => PathIds(Path);

    public void Rename(string name) => Name = RequireName(name);

    public void Deactivate() => Status = UnitStatus.Inactive;

    public void Reactivate()
    {
        if (MergedIntoId is not null)
        {
            throw new InvalidOperationException("A merged unit stays inactive.");
        }

        Status = UnitStatus.Active;
    }

    /// <summary>The end of a merge: everything this unit held is in the other unit, and this one is inactive.</summary>
    public void MergeInto(OrganisationUnit into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (Contains(into))
        {
            throw new InvalidOperationException("A unit cannot merge into itself or one of its descendants.");
        }

        Status = UnitStatus.Inactive;
        MergedIntoId = into.Id;
    }

    /// <summary>The unit's own row; the descendants' paths are rewritten by the service in one statement (ADR-0030).</summary>
    public void MoveUnder(OrganisationUnit parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (Contains(parent))
        {
            throw new InvalidOperationException("A unit cannot move under itself or one of its descendants.");
        }

        ParentId = parent.Id;
        Depth = parent.Depth + 1;
        Path = parent.Path + Segment(Id) + "/";
    }

    public static string Segment(Guid id) => id.ToString("N");

    public static IReadOnlyList<Guid> PathIds(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(segment => Guid.ParseExact(segment, "N")).ToList();
    }

    public static string NormaliseCode(string? code) => (code ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValidCode(string? code) => code is { Length: > 0 and <= CodeMaxLength } && CodePattern().IsMatch(code);

    public static bool IsValidName(string? name) => name is not null && name.Trim().Length is > 0 and <= NameMaxLength;

    private static string RequireName(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"A unit needs a name of 1 to {NameMaxLength} characters.", nameof(name));
        }

        return name.Trim();
    }

    private static string RequireCode(string code)
    {
        var normalised = NormaliseCode(code);
        if (!IsValidCode(normalised))
        {
            throw new ArgumentException($"'{code}' is not a unit code. Use lowercase letters, digits and hyphens, at most {CodeMaxLength} characters.", nameof(code));
        }

        return normalised;
    }

    private static string RequireKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > KindMaxLength)
        {
            throw new ArgumentException($"A unit needs a kind code of at most {KindMaxLength} characters.", nameof(kind));
        }

        return kind;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
