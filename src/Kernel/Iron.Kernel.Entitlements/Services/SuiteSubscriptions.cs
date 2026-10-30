using Iron.Kernel.Api.Modules;
using Iron.Kernel.Entitlements.Contracts;

namespace Iron.Kernel.Entitlements.Services;

/// <summary>
/// A suite's subscription in this unit (TF-244, ADR-0059), for the suite's own pages: the plan the tenant holds for the
/// suite and its grace days, from the document, and every licensed module and feature of the suite this unit loaded,
/// with what applies now from the one evaluator and the dates from the document.
/// </summary>
public sealed class SuiteSubscriptions(EntitlementEvaluator entitlements, EntitlementCache cache, LicensingCatalogue catalogue) : ISuiteSubscriptions
{
    public SuiteSubscription Of(string suite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suite);
        var document = cache.Current?.Document;
        var plan = document?.FindSuite(suite);
        var modules = catalogue.Modules.Where(m => m.Suite == suite).Select(module =>
        {
            var entry = document?.FindModule(module.Code);
            var now = entitlements.Module(module.Code);
            return new SubscribedEntitlement(module.Code, module.Module, null, now.Status, now.Reason, StartsOn(entry?.StartsOn), entry?.EndsOn, GraceEnds(document, entry?.EndsOn, entry?.GraceDays));
        }).ToList();
        var features = catalogue.Features.Where(f => f.Suite == suite).Select(feature =>
        {
            var entry = document?.FindFeature(feature.Code);
            var now = entitlements.Feature(feature.Code);
            return new SubscribedEntitlement(feature.Code, feature.Module, feature.Description, now.Status, now.Reason, StartsOn(entry?.StartsOn), entry?.EndsOn, GraceEnds(document, entry?.EndsOn, entry?.GraceDays));
        }).ToList();
        return new SuiteSubscription(suite, plan?.Plan, plan?.GraceDays ?? 0, entitlements.Snapshot is null, modules, features);
    }

    private static DateOnly? GraceEnds(EntitlementsDocument? document, DateOnly? endsOn, int? graceDays) =>
        document is not null && endsOn is { } end ? EntitlementRules.GraceEnds(end, EntitlementRules.GraceDaysOf(document, graceDays)) : null;

    /// <summary>The configuration source starts from the beginning of time; the page shows no start for it.</summary>
    private static DateOnly? StartsOn(DateOnly? startsOn) => startsOn is { } start && start != DateOnly.MinValue ? start : null;
}
