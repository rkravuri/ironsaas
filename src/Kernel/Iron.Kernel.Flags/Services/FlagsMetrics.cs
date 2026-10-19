using System.Diagnostics.Metrics;

namespace Iron.Kernel.Flags.Services;

/// <summary>
/// The flag meter (ADR-0035, TF-050 AC3: the alert): <c>flags.unknown</c> is 1 while the unit evaluates on the code
/// defaults because it has never known any rules, <c>flags.age</c> the seconds since the store last confirmed the rules,
/// <c>flags.refresh_failures</c> counts reads that failed, and <c>flags.refusals</c> requests refused, by error code.
/// </summary>
public sealed class FlagsMetrics : IDisposable
{
    public const string MeterName = "Iron.Flags";

    private readonly Meter _meter;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _refusals;

    public FlagsMetrics(FlagRulesCache cache, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(time);
        _meter = new Meter(MeterName);
        _failures = _meter.CreateCounter<long>("flags.refresh_failures", description: "Reads of the flag rules from the store that failed.");
        _refusals = _meter.CreateCounter<long>("flags.refusals", description: "Requests refused by a flag, by error code: flags.killed or flags.disabled.");
        _meter.CreateObservableGauge("flags.unknown", () => cache.Current is null ? 1 : 0, description: "1 while the unit knows no rules at all and evaluates on the code defaults.");
        _meter.CreateObservableGauge("flags.age", () => cache.Current is { } current ? (time.GetUtcNow() - current.ConfirmedAt).TotalSeconds : double.NaN, unit: "s", description: "Seconds since the store last confirmed the flag rules.");
    }

    public void RefreshFailed() => _failures.Add(1);

    public void Refused(string errorCode) => _refusals.Add(1, new KeyValuePair<string, object?>("error_code", errorCode));

    public void Dispose() => _meter.Dispose();
}
