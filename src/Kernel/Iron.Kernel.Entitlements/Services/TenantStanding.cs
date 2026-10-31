using Iron.Kernel.Entitlements.Contracts;

namespace Iron.Kernel.Entitlements.Services;

/// <summary>The tenant's standing from the last entitlements document the unit confirmed or restored (ADR-0061).</summary>
public sealed class TenantStanding(EntitlementCache cache) : ITenantStanding
{
    public bool Suspended => cache.Current?.Document.Suspended == true;
}
