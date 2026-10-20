using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iron.Kernel.Flags.Services;

/// <summary>
/// The flags in the readiness detail (ADR-0035). Degraded, never unhealthy: with last known rules the unit is right and
/// only possibly late; with none it runs on the code defaults, which the description says, and the alert is raised.
/// </summary>
public sealed class FlagsHealthCheck(FlagRulesCache cache, IFlagRuleSource source) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var current = cache.Current;
        var failure = cache.LastFailure;
        var because = failure is { } failed ? $" The last error: {failed.Message}" : string.Empty;
        if (current is null)
        {
            return Task.FromResult(HealthCheckResult.Degraded($"No flag rules are known: {source.Describe()} has not answered since the unit started, so every flag has its code default.{because}"));
        }

        if (failure is { } stale)
        {
            return Task.FromResult(HealthCheckResult.Degraded($"{source.Describe()} has not answered since {stale.At:o}; the rules confirmed at {current.ConfirmedAt:o} stand until it does.{because}"));
        }

        return Task.FromResult(HealthCheckResult.Healthy($"Flag rules confirmed at {current.ConfirmedAt:o}: platform revision {current.Rules.Platform?.Revision ?? 0}, tenant revision {current.Rules.Tenant?.Revision ?? 0}."));
    }
}
