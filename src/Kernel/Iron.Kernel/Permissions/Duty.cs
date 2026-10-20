using System.Text.RegularExpressions;

namespace Iron.Kernel;

/// <summary>
/// A duty a person can hand to someone else for a while (TF-037, ADR-0037): a named set of permissions, declared by the
/// module that owns them, as features and settings are. A delegate acting for the delegator holds exactly the
/// delegator's current permissions among these, at the delegator's scopes. The kernel's <see cref="KernelDuties.All"/> covers
/// every permission the delegator holds.
/// </summary>
public sealed partial class Duty : IEquatable<Duty>
{
    public Duty(string code, string name, string description, IReadOnlyList<Permission> permissions)
    {
        if (!IsValidCode(code))
        {
            throw new ArgumentException($"'{code}' is not a duty code. Use dotted lowercase words, for example identity.user_administration.", nameof(code));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(permissions);
        if (!string.Equals(code, KernelDuties.AllCode, StringComparison.Ordinal) && permissions.Count == 0)
        {
            throw new ArgumentException($"The duty '{code}' covers no permission.", nameof(permissions));
        }

        Code = code;
        Name = name.Trim();
        Description = description.Trim();
        Permissions = [.. permissions.Distinct()];
    }

    public string Code { get; }

    /// <summary>The duty in a few words, as people read it: "user administration".</summary>
    public string Name { get; }

    public string Description { get; }

    /// <summary>The permissions the duty covers; empty for <see cref="KernelDuties.All"/>, which covers all of them.</summary>
    public IReadOnlyList<Permission> Permissions { get; }

    public bool IsAll => string.Equals(Code, KernelDuties.AllCode, StringComparison.Ordinal);

    public static bool IsValidCode(string? code) => code is not null && CodePattern().IsMatch(code);

    public bool Equals(Duty? other) => other is not null && string.Equals(Code, other.Code, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as Duty);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Code);

    public override string ToString() => Code;

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

/// <summary>The kernel's own duty: all of them. Modules declare the others in their <c>IModule.Duties</c>.</summary>
public static class KernelDuties
{
    public const string AllCode = "all";

    /// <summary>Every permission the delegator holds, at the scopes they hold it: a full stand-in for a while.</summary>
    public static readonly Duty All = new(AllCode, "all duties", "Everything the person may do, at every scope they may do it.", []);
}
