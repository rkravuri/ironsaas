using Iron.Kernel.Api.Context;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Entitlements.Services;

namespace Iron.Kernel.Entitlements.Api;

/// <summary>
/// The <c>entitlements</c> part of <c>GET /api/v1/me/context</c> (ADR-0034): every licensed module (with the person's
/// seat taken into account) and every feature of this build, with status and reason, and how fresh the answer is.
/// </summary>
public sealed class EntitlementsContextPart(EntitlementEvaluator entitlements, EntitlementCache cache, LicensingCatalogue catalogue) : IContextPart
{
    public const string PartName = "entitlements";

    public string Name => PartName;

    public async Task<object> BuildAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var modules = new List<ContextEntry>();
        foreach (var module in catalogue.Modules)
        {
            var entitlement = await entitlements.ModuleForAsync(module.Code, principalId, cancellationToken).ConfigureAwait(false);
            modules.Add(new ContextEntry(module.Code, entitlement.Status, entitlement.Reason));
        }

        var features = catalogue.Features.Select(f => new ContextEntry(f.Code, entitlements.Feature(f.Code).Status, entitlements.Feature(f.Code).Reason)).ToList();
        var current = cache.Current;
        return new ContextEntitlements(current?.Document.Revision ?? 0, current?.Document.Plan, current?.ConfirmedAt, entitlements.Snapshot is null, modules, features);
    }
}
