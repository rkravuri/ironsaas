namespace Iron.Kernel.Entitlements.Services;

/// <summary>
/// Bound from the <c>Entitlements</c> section. A unit reads its entitlements from the store; a host without a store
/// (the laptop's <c>make run-api</c>, the control plane, which is no tenant) reads them from this section instead.
/// </summary>
public sealed class EntitlementsOptions
{
    public const string SectionName = "Entitlements";
    public const string StoreSource = "store";
    public const string ConfigurationSource = "configuration";

    /// <summary><c>store</c> or <c>configuration</c>; empty means the store when one is configured, else this section.</summary>
    public string? Source { get; set; }

    /// <summary>
    /// How often the unit reads the store (ADR-0034): 30 seconds, so the unit and the shell, each holding an answer at
    /// most this long, show a change everywhere within a minute (TF-043). At most 60.
    /// </summary>
    public int RefreshSeconds { get; set; } = 30;

    /// <summary>How long the last known answer stands while the store does not answer (TF-043 AC3).</summary>
    public int StaleAfterHours { get; set; } = 24;

    /// <summary>How long the first read may hold up the start of the host before it serves on what it has.</summary>
    public int StartTimeoutSeconds { get; set; } = 5;

    /// <summary>The configuration source's features, all enabled from the start of time.</summary>
    public List<string> Features { get; set; } = [];

    /// <summary>The configuration source's licensed modules.</summary>
    public List<ConfiguredModule> Modules { get; set; } = [];

    public int GraceDays { get; set; }

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 1, 60));

    public TimeSpan StaleAfter => TimeSpan.FromHours(Math.Max(1, StaleAfterHours));

    public sealed class ConfiguredModule
    {
        public string Code { get; set; } = string.Empty;

        public EntitlementStatus Status { get; set; } = EntitlementStatus.Enabled;

        public SubscriptionType SubscriptionType { get; set; } = SubscriptionType.Platform;

        public int? UserLimit { get; set; }

        public DateOnly? StartsOn { get; set; }

        public DateOnly? EndsOn { get; set; }
    }
}
