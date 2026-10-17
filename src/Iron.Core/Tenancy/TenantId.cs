using System.Diagnostics.CodeAnalysis;

namespace Iron.Core.Tenancy;

/// <summary>
/// The stable identifier of a tenant: a short lowercase slug such as <c>acme</c>. It is chosen when the tenant
/// is created and never changes; it is the same in every environment (TF-001). It names the tenant's database,
/// containers, secrets path and default hostname, so the rules are strict: 3 to 30 characters, lowercase letters,
/// digits and single hyphens, starting with a letter and not ending with a hyphen.
/// </summary>
public readonly record struct TenantId
{
    public const int MinLength = 3;
    public const int MaxLength = 30;

    /// <summary>The identifier the control plane uses for itself in audit events and telemetry.</summary>
    public static readonly TenantId ControlPlane = new("control");

    private readonly string? _value;

    private TenantId(string value)
    {
        _value = value;
    }

    public string Value => _value ?? string.Empty;

    public bool IsEmpty => _value is null;

    public static TenantId Parse(string value)
    {
        return TryParse(value, out var id)
            ? id
            : throw new FormatException(
                $"'{value}' is not a valid tenant identifier. Use {MinLength} to {MaxLength} lowercase letters, digits and single hyphens, starting with a letter.");
    }

    public static bool TryParse(string? value, out TenantId id)
    {
        if (IsValid(value))
        {
            id = new TenantId(value);
            return true;
        }

        id = default;
        return false;
    }

    public static bool IsValid([NotNullWhen(true)] string? value)
    {
        if (value is null || value.Length < MinLength || value.Length > MaxLength)
        {
            return false;
        }

        if (!char.IsAsciiLetterLower(value[0]) || value[^1] == '-' || value.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-')
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => Value;
}
