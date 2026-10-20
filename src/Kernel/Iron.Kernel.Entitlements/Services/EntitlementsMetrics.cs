using System.Diagnostics.Metrics;

namespace Iron.Kernel.Entitlements.Services;

/// <summary>
/// The entitlement meter (TF-043 AC3: the alert): <c>entitlements.stale</c> is 1 while the unit has no answer younger
/// than 24 hours, <c>entitlements.age</c> the seconds since the store last confirmed one, <c>entitlements.refresh_failures</c>
/// counts reads that failed, and <c>entitlements.refusals</c> counts requests refused, by error code.
/// </summary>
public sealed class EntitlementsMetrics : IDisposable
{
    public const string MeterName = "Iron.Entitlements";

    private readonly Meter _meter;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _refusals;

    public EntitlementsMetrics(EntitlementCache cache, EntitlementsOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _meter = new Meter(MeterName);
        _failures = _meter.CreateCounter<long>("entitlements.refresh_failures", description: "Reads of the entitlements from the store that failed.");
        _refusals = _meter.CreateCounter<long>("entitlements.refusals", description: "Requests refused because a module or a feature is not entitled, by error code.");
        _meter.CreateObservableGauge("entitlements.stale", () => cache.Usable(time.GetUtcNow(), options.StaleAfter) is null ? 1 : 0, description: "1 while the unit has no entitlements answer younger than the limit: everything licensed is treated as disabled.");
        _meter.CreateObservableGauge("entitlements.age", () => cache.Current is { } current ? (time.GetUtcNow() - current.ConfirmedAt).TotalSeconds : double.NaN, unit: "s", description: "Seconds since the store last confirmed the entitlements.");
    }

    public void RefreshFailed() => _failures.Add(1);

    public void Refused(string errorCode) => _refusals.Add(1, new KeyValuePair<string, object?>("error_code", errorCode));

    public void Dispose() => _meter.Dispose();
}
