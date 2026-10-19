using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iron.Kernel.Settings.Services;

/// <summary>
/// The settings in the readiness detail (ADR-0036). Degraded, never unhealthy, and only once the values have failed to
/// refresh for five minutes (TF-059 AC4): until then the unit answers the last values and is at most a little late.
/// </summary>
public sealed class SettingsHealthCheck(SettingsCache cache, SettingsOptions options, TimeProvider time) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var current = cache.Current;
        if (cache.Failing is { } failing && time.GetUtcNow() - failing.Since >= options.AlertAfter)
        {
            var stand = current is null ? "every setting has its default" : $"the values read at {current.ReadAt:o} stand";
            return Task.FromResult(HealthCheckResult.Degraded($"The settings have not refreshed since {failing.Since:o}; {stand}. The last error: {failing.Message}"));
        }

        return Task.FromResult(current is null
            ? HealthCheckResult.Healthy("The settings have not been read yet; every setting has its default.")
            : HealthCheckResult.Healthy($"Settings read at {current.ReadAt:o}: {current.Count} value(s) set."));
    }
}
