using System.Diagnostics.Metrics;

namespace Iron.Kernel.Settings.Services;

/// <summary>
/// The settings meter (ADR-0036, TF-059 AC4: the alert): <c>settings.stale</c> is 1 once the values have failed to refresh
/// for five minutes, <c>settings.age</c> the seconds since the last good read, <c>settings.refresh_failures</c> counts
/// reads that failed, and <c>settings.secret_reads</c> the reads of the secret store for secret references.
/// </summary>
public sealed class SettingsMetrics : IDisposable
{
    public const string MeterName = "Iron.Settings";

    private readonly Meter _meter;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _secretReads;

    public SettingsMetrics(SettingsCache cache, SettingsOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _meter = new Meter(MeterName);
        _failures = _meter.CreateCounter<long>("settings.refresh_failures", description: "Reads of the settings that failed.");
        _secretReads = _meter.CreateCounter<long>("settings.secret_reads", description: "Reads of the secret store for secret-reference settings.");
        _meter.CreateObservableGauge("settings.stale", () => cache.Failing is { } failing && time.GetUtcNow() - failing.Since >= options.AlertAfter ? 1 : 0, description: "1 once the settings have failed to refresh for five minutes; the last values stand.");
        _meter.CreateObservableGauge("settings.age", () => cache.Current is { } current ? (time.GetUtcNow() - current.ReadAt).TotalSeconds : double.NaN, unit: "s", description: "Seconds since the settings were last read.");
    }

    public void RefreshFailed() => _failures.Add(1);

    public void SecretRead() => _secretReads.Add(1);

    public void Dispose() => _meter.Dispose();
}
