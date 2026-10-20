using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// The entitlement and flag gates of a test host that runs neither the Entitlements nor the Flags module: every module
/// and feature is entitled and no kill switch is on, so the tests here prove their own module's rules.
/// Iron.Kernel.Entitlements.Tests and Iron.Kernel.Flags.Tests prove the real gates (ADR-0034, ADR-0035).
/// </summary>
public sealed class AllEntitled : IEntitlementGate, IFlagGate
{
    public ValueTask<EntitlementDecision> CheckAsync(EntitlementRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(EntitlementDecision.Allow);

    public ValueTask<FlagDecision> CheckAsync(FlagRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(FlagDecision.Allow);
}
