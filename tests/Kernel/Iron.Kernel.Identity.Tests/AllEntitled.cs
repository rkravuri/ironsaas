using Iron.Kernel.Api.Entitlements;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// The entitlement gate of a test host that runs no Entitlements module: every module and feature is entitled, so the
/// tests here prove their own module's rules. Iron.Kernel.Entitlements.Tests proves the real gate (ADR-0034).
/// </summary>
public sealed class AllEntitled : IEntitlementGate
{
    public ValueTask<EntitlementDecision> CheckAsync(EntitlementRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(EntitlementDecision.Allow);
}
