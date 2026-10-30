using Iron.Kernel.Entitlements.Contracts;

namespace Iron.Kernel.Entitlements.Services;

/// <summary>
/// The rules of an entitlement, pure so they are tested on dates alone (TF-040, TF-046). A subscription runs from its
/// start date to its end date, both included, in UTC. After the end date the grace days follow, in which the status the
/// control plane set still applies; then it is Disabled with the reason <c>expired</c>. Expired is never stored.
/// </summary>
public static class EntitlementRules
{
    public static Entitlement Evaluate(EntitlementStatus status, DateOnly startsOn, DateOnly? endsOn, int graceDays, DateOnly today)
    {
        if (today < startsOn)
        {
            return new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.NotStarted);
        }

        if (status == EntitlementStatus.Disabled)
        {
            return new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.SwitchedOff);
        }

        if (endsOn is { } end && today > end)
        {
            return today <= GraceEnds(end, graceDays)
                ? new Entitlement(status, EntitlementReasons.InGrace)
                : new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.Expired);
        }

        return new Entitlement(status, EntitlementReasons.Licensed);
    }

    /// <summary>The last day of the grace period after an end date.</summary>
    public static DateOnly GraceEnds(DateOnly endsOn, int graceDays) => endsOn.AddDays(Math.Max(0, graceDays));

    /// <summary>
    /// The grace days after an entry's end date: its own plan's (TF-244, a suite's plan has its own, ADR-0059), or the
    /// document's for an entry written before plans per suite.
    /// </summary>
    public static int GraceDaysOf(EntitlementsDocument document, int? entryGraceDays)
    {
        ArgumentNullException.ThrowIfNull(document);
        return entryGraceDays ?? document.GraceDays;
    }

    /// <summary>A licensed module from the document: not in it is not licensed.</summary>
    public static Entitlement Module(EntitlementsDocument document, string licence, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(document);
        var entry = document.FindModule(licence);
        return entry is null
            ? new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.NotLicensed)
            : Evaluate(entry.Status, entry.StartsOn, entry.EndsOn, GraceDaysOf(document, entry.GraceDays), today);
    }

    /// <summary>
    /// A feature from the document, governed by its module (TF-046 AC3): a module that is not available makes its
    /// features unavailable, and a read-only module makes them read only.
    /// </summary>
    public static Entitlement Feature(EntitlementsDocument document, string feature, Entitlement? module, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (module is { IsAvailable: false })
        {
            return new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.ModuleUnavailable);
        }

        var entry = document.FindFeature(feature);
        var own = entry is null
            ? new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.NotLicensed)
            : Evaluate(entry.Status, entry.StartsOn, entry.EndsOn, GraceDaysOf(document, entry.GraceDays), today);
        return module is { Status: EntitlementStatus.ReadOnly } && own.Status == EntitlementStatus.Enabled
            ? new Entitlement(EntitlementStatus.ReadOnly, own.Reason)
            : own;
    }
}
