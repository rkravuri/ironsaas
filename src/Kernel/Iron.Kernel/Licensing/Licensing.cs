using System.Text.RegularExpressions;

namespace Iron.Kernel;

/// <summary>How a licensed module is sold (TF-040): per person with seats, per transaction, or as a platform fee.</summary>
public enum SubscriptionType
{
    PerPerson,
    PerTransaction,
    Platform,
}

/// <summary>
/// The status of a module or a feature for a tenant (TF-040): what the control plane stores, and what the unit answers
/// after dates, grace and the governing module are taken into account. Expired is never stored; it is a reason.
/// </summary>
public enum EntitlementStatus
{
    Enabled,
    ReadOnly,
    Disabled,
}

/// <summary>
/// How a Business or suite module is licensed (ADR-0034): its licence code, which plans and entitlements name, and
/// its subscription type. Kernel modules are the platform and declare none.
/// </summary>
public sealed partial record ModuleLicence
{
    public ModuleLicence(string code, SubscriptionType subscriptionType)
    {
        if (!IsValidCode(code))
        {
            throw new ArgumentException($"'{code}' is not a licence code. Use lowercase letters, digits and underscores, starting with a letter.", nameof(code));
        }

        Code = code;
        SubscriptionType = subscriptionType;
    }

    public string Code { get; }

    public SubscriptionType SubscriptionType { get; }

    public static bool IsValidCode(string? code) => code is not null && code.Length <= 64 && CodePattern().IsMatch(code);

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

/// <summary>
/// A part of a module that is licensed on its own (TF-046): <c>authorization.role_transfer</c>. A module declares its
/// features in its Contracts and lists them in its <c>IModule</c>; endpoints name them with <c>RequireFeature</c>.
/// </summary>
public sealed partial record Feature
{
    public Feature(string code, string description)
    {
        if (!IsValidCode(code))
        {
            throw new ArgumentException($"'{code}' is not a feature code. Use area.feature in lowercase, for example authorization.role_transfer.", nameof(code));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Code = code;
        Description = description;
    }

    public string Code { get; }

    public string Description { get; }

    public static bool IsValidCode(string? code) => code is not null && code.Length <= 100 && CodePattern().IsMatch(code);

    [GeneratedRegex("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

/// <summary>An answer of the entitlement service (TF-043): the status that applies now, and why, in a word the shell translates.</summary>
public sealed record Entitlement(EntitlementStatus Status, string Reason)
{
    public static readonly Entitlement Platform = new(EntitlementStatus.Enabled, EntitlementReasons.Platform);

    public bool IsAvailable => Status != EntitlementStatus.Disabled;

    public bool AllowsWrites => Status == EntitlementStatus.Enabled;
}

/// <summary>Why an entitlement answers as it does. The words are a contract with the shell's catalogue.</summary>
public static class EntitlementReasons
{
    /// <summary>A kernel module: always on, never in a plan.</summary>
    public const string Platform = "platform";

    /// <summary>Enabled or read only, as the control plane set it, within the subscription dates.</summary>
    public const string Licensed = "licensed";

    /// <summary>In the subscription, and switched off by the control plane.</summary>
    public const string SwitchedOff = "switched_off";

    /// <summary>The plan does not include it.</summary>
    public const string NotLicensed = "not_licensed";

    /// <summary>The subscription starts later.</summary>
    public const string NotStarted = "not_started";

    /// <summary>The end date has passed and the grace period runs: it still works.</summary>
    public const string InGrace = "in_grace";

    /// <summary>The end date and the grace period have passed.</summary>
    public const string Expired = "expired";

    /// <summary>A feature whose module is not available: the module governs (TF-046 AC3).</summary>
    public const string ModuleUnavailable = "module_unavailable";

    /// <summary>A per-person module and a person without a seat (TF-041).</summary>
    public const string NoSeat = "no_seat";

    /// <summary>The unit has had no answer from the store for 24 hours, or never had one (TF-043 AC3).</summary>
    public const string Unknown = "unknown";
}
