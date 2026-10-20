namespace Iron.Kernel.Flags.Services;

/// <summary>
/// Bound from the <c>Flags</c> section. A unit reads its rules from the store; the control plane, the fleet and the laptop's
/// <c>make run-api</c> read this section instead. The provider is chosen here (TF-056): a name this build does not have,
/// or a source it cannot read, stops the host at start with a message that says so.
/// </summary>
public sealed class FlagsOptions
{
    public const string SectionName = "Flags";
    public const string IronProvider = "iron";
    public const string StoreSource = "store";
    public const string ConfigurationSource = "configuration";

    /// <summary>The OpenFeature provider: <c>iron</c>, the in-house provider over the rules (ADR-0035), is the only one today.</summary>
    public string Provider { get; set; } = IronProvider;

    /// <summary><c>store</c> or <c>configuration</c>; empty means the store when one is configured, else this section.</summary>
    public string? Source { get; set; }

    /// <summary>How often the unit reads the store: 30 seconds, as for entitlements (ADR-0034), at most 60.</summary>
    public int RefreshSeconds { get; set; } = 30;

    /// <summary>How long the first read may hold up the start of the host before it serves on what it has.</summary>
    public int StartTimeoutSeconds { get; set; } = 5;

    /// <summary>The configuration source's rules: global, unconditional, one per flag.</summary>
    public List<ConfiguredRule> Rules { get; set; } = [];

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 1, 60));

    public static IReadOnlyList<string> Providers => [IronProvider];

    public sealed class ConfiguredRule
    {
        public string Flag { get; set; } = string.Empty;

        /// <summary>The value as JSON: <c>true</c>, <c>"warm"</c>, <c>3</c>.</summary>
        public string Value { get; set; } = "null";

        public string? Reason { get; set; }
    }
}
