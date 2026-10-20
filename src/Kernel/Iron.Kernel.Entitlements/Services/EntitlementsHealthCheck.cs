using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iron.Kernel.Entitlements.Services;

/// <summary>
/// The entitlements in the readiness detail (TF-043 AC3, TF-133). Degraded, not unhealthy: the unit keeps serving the
/// platform while it cannot confirm what is licensed, and the probe still answers 200; the description says why.
/// </summary>
public sealed class EntitlementsHealthCheck(EntitlementCache cache, EntitlementsOptions options, IEntitlementSource source, TimeProvider time) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var current = cache.Current;
        var failure = cache.LastFailure;
        if (current is null || now - current.ConfirmedAt > options.StaleAfter)
        {
            var since = current is null ? "never" : $"last at {current.ConfirmedAt:o}";
            return Task.FromResult(HealthCheckResult.Degraded($"The entitlements are unknown: {source.Describe()} confirmed them {since}. Every licensed module and feature is treated as disabled until it answers.{Because(failure)}"));
        }

        if (failure is { } failed)
        {
            return Task.FromResult(HealthCheckResult.Degraded($"{source.Describe()} has not answered since {failed.At:o}; revision {current.Document.Revision}, confirmed at {current.ConfirmedAt:o}, stands until {current.ConfirmedAt + options.StaleAfter:o}.{Because(failure)}"));
        }

        return Task.FromResult(HealthCheckResult.Healthy($"Revision {current.Document.Revision} of the entitlements, confirmed at {current.ConfirmedAt:o}."));
    }

    private static string Because((DateTimeOffset At, string Message)? failure) => failure is { } failed ? $" The last error: {failed.Message}" : string.Empty;
}
