namespace Iron.Kernel.Tenancy.Services;

/// <summary>Bound from the <c>Tenancy</c> configuration section of the control plane.</summary>
public sealed class TenancyOptions
{
    public const string SectionName = "Tenancy";

    /// <summary>A tenant without hostnames gets <c>&lt;slug&gt;.&lt;BaseDomain&gt;</c>: <c>acme.localhost</c> on the laptop.</summary>
    public string BaseDomain { get; set; } = "localhost";

    public string DefaultRegion { get; set; } = "local";

    public string DefaultPlan { get; set; } = "standard";
}
