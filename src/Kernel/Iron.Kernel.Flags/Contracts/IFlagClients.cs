using Iron.Kernel.History;
using OpenFeature;

namespace Iron.Kernel.Flags.Contracts;

/// <summary>
/// How product code asks a flag (TF-050, TF-056): an OpenFeature client whose evaluation context is the current request's
/// tenant, region, plan, principal and home unit. The provider behind it is the configured one (<c>Flags:Provider</c>);
/// product code never names it.
/// <code>
/// var flags = await clients.CurrentAsync(cancellationToken);
/// var tone = await flags.GetStringValueAsync("flags.example_tone", "plain");
/// </code>
/// </summary>
public interface IFlagClients
{
    /// <summary>A client for the signed-in principal of the request, or for the tenant alone outside one.</summary>
    Task<IFeatureClient> CurrentAsync(CancellationToken cancellationToken);

    /// <summary>A client for one principal of this tenant.</summary>
    Task<IFeatureClient> ForAsync(Guid principalId, CancellationToken cancellationToken);
}

/// <summary>The keys of the evaluation context the API builds (ADR-0035). The targeting key is the principal's id.</summary>
public static class FlagContextKeys
{
    public const string Tenant = "tenant";
    public const string Region = FlagAttributes.Region;
    public const string Plan = FlagAttributes.Plan;
    public const string PrincipalType = FlagAttributes.PrincipalType;
    public const string HomeUnit = "home_unit";

    /// <summary>The home unit and every unit above it, nearest first, so a unit rule matches below its unit.</summary>
    public const string Units = "units";
}

/// <summary>Two harmless flags for the proof (A5): they change nothing a person sees, and go when the first real ones arrive.</summary>
public static class ExampleFlags
{
    public const string Owner = "Platform team";

    public static readonly Flag Release = Flag.Boolean("flags.example_release", FlagKind.Release, "An example release flag: changes nothing; proves rules, scopes and rollouts end to end.", Owner, new DateOnly(2027, 3, 31), false);

    public static readonly Flag Tone = Flag.String("flags.example_tone", FlagKind.Experiment, "An example experiment flag with the values warm and plain: changes nothing; proves string flags.", Owner, new DateOnly(2027, 3, 31), "plain");

    public static IReadOnlyList<Flag> All => [Release, Tone];
}

/// <summary>Audit event types raised by the Flags module (TF-060).</summary>
public static class FlagsAuditEvents
{
    public const string AccessDenied = "flags.access_denied";

    /// <summary>Every event type above with the words it reads as in the audit trail, the History tab and the exports (ADR-0051).</summary>
    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        new(AccessDenied, "Refused by a feature flag"),
    ];
}
