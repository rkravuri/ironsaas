using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.RateLimiting;
using Iron.Kernel.Api.Http;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Protection.Contracts;
using Iron.Kernel.Settings.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Protection.Services;

/// <summary>One fixed window of one limit for one subject: the key names all three, so a changed limit starts a new window.</summary>
public sealed record RateLimitTarget(string Key, FixedWindowRateLimiterOptions Options);

/// <summary>
/// The process's rate limiters (TF-072, ADR-0053): one fixed window per limit, subject and value, made when first used and
/// dropped when idle. Counted in this process for now; S6, with several replicas per unit, moves them to a shared store.
/// </summary>
public sealed class RateLimitGate : IDisposable
{
    private const int MaxRemembered = 10_000;

    private readonly PartitionedRateLimiter<RateLimitTarget> _limiter =
        PartitionedRateLimiter.Create<RateLimitTarget, string>(target => RateLimitPartition.GetFixedWindowLimiter(target.Key, _ => target.Options));

    private readonly ConcurrentDictionary<string, DateTimeOffset> _refused = new(StringComparer.Ordinal);

    public RateLimitLease Acquire(RateLimitTarget target) => _limiter.AttemptAcquire(target);

    /// <summary>True for the first refusal of a key in its window: the audit trail keeps one event a window, not one a request.</summary>
    public bool FirstRefusal(string key, TimeSpan window, DateTimeOffset now)
    {
        if (_refused.Count > MaxRemembered)
        {
            foreach (var (stale, until) in _refused)
            {
                if (until <= now)
                {
                    _refused.TryRemove(stale, out _);
                }
            }
        }

        var windowEnds = now + window;
        var first = true;
        _refused.AddOrUpdate(key, windowEnds, (_, until) =>
        {
            first = until <= now;
            return first ? windowEnds : until;
        });
        return first;
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// Counts every request under <c>/api</c> and <c>/auth</c> against the API limit of its principal (its address before
/// sign-in), and an endpoint's own limits on top: sign-in per address, recovery per account, exports per principal, CSP
/// reports per address. A refusal answers 429 with <c>Retry-After</c> and <c>protection.rate_limited</c>, is counted,
/// and writes one low-severity audit event per subject and window (TF-072 AC1, AC2). The tenant's values are read from
/// its settings at every request, held to the operator's bounds, so a change applies within the settings' refresh, at
/// most 30 seconds (AC3).
/// </summary>
public sealed class RateLimitMiddleware(RequestDelegate next, RateLimitGate gate, ProtectionMetrics metrics, ProtectionOptions options, TimeProvider time, ILogger<RateLimitMiddleware> logger)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    public async Task InvokeAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var path = http.Request.Path;
        var counted = path.StartsWithSegments("/api") || path.StartsWithSegments("/auth") || path.StartsWithSegments("/connect");
        var own = http.GetEndpoint()?.Metadata.GetOrderedMetadata<RateLimitedMetadata>() ?? [];
        if (!counted && own.Count == 0)
        {
            await next(http).ConfigureAwait(false);
            return;
        }

        var limits = new List<(string Policy, string SubjectKind, string Subject)>();
        if (counted)
        {
            limits.Add(PerPrincipal(http, RateLimitPolicies.Api));
        }

        foreach (var metadata in own.DistinctBy(m => m.Policy))
        {
            limits.Add(metadata.Policy switch
            {
                RateLimitPolicies.Exports => PerPrincipal(http, RateLimitPolicies.Exports),
                RateLimitPolicies.Recovery when metadata.SubjectRouteValue is { } name && http.GetRouteValue(name) is { } account => (metadata.Policy, "account", $"account:{account}"),
                _ => (metadata.Policy, "address", $"address:{Address(http)}"),
            });
        }

        foreach (var (policy, kind, subject) in limits)
        {
            var (permits, window) = await LimitAsync(http, policy).ConfigureAwait(false);
            var key = string.Create(CultureInfo.InvariantCulture, $"{policy}|{permits}|{subject}");
            using var lease = gate.Acquire(new RateLimitTarget(key, new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0, AutoReplenishment = true }));
            if (!lease.IsAcquired)
            {
                var wait = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : window;
                await RefuseAsync(http, policy, kind, permits, window, key, wait).ConfigureAwait(false);
                return;
            }
        }

        await next(http).ConfigureAwait(false);
    }

    private (string Policy, string SubjectKind, string Subject) PerPrincipal(HttpContext http, string policy)
    {
        var principal = http.RequestServices.GetService<ICurrentPrincipal>()?.Id ?? Guid.Empty;
        return principal == Guid.Empty
            ? (policy, "address", $"address:{Address(http)}")
            : (policy, "principal", $"principal:{principal:N}");
    }

    private static string Address(HttpContext http) => ClientAddress.Of(http)?.ToString() ?? "unknown";

    /// <summary>The tenant's value of a limit, held to the operator's bounds; the CSP reports' limit is the operator's alone.</summary>
    private async Task<(int Permits, TimeSpan Window)> LimitAsync(HttpContext http, string policy)
    {
        if (policy == RateLimitPolicies.CspReports)
        {
            return (Math.Max(1, options.CspReports.PerMinute), Minute);
        }

        var setting = ProtectionSettings.OfLimit(policy) ?? throw new InvalidOperationException($"No setting holds the rate limit '{policy}'.");
        var value = setting.DefaultValue;
        if (http.RequestServices.GetService<ISettings>() is { } settings)
        {
            value = await settings.GetAsync(setting, SettingContext.Tenant, http.RequestAborted).ConfigureAwait(false);
        }

        var window = policy is RateLimitPolicies.Recovery or RateLimitPolicies.Exports ? Hour : Minute;
        return (RateLimitBounds.Clamp(policy, value), window);
    }

    private async Task RefuseAsync(HttpContext http, string policy, string kind, int permits, TimeSpan window, string key, TimeSpan wait)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        metrics.RateLimited(policy);
        var now = time.GetUtcNow();
        if (gate.FirstRefusal(key, window, now))
        {
            logger.LogWarning("The {Limit} limit ({Permits} per {Window}) refused a request to {Path} by its {SubjectKind}; refusals in this window are counted, not logged.", policy, permits, window, http.Request.Path.Value, kind);
            if (http.RequestServices.GetService<IAuditWriter>() is { } audit)
            {
                var principal = http.RequestServices.GetService<ICurrentPrincipal>()?.Id ?? Guid.Empty;
                var details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["limit"] = policy,
                    ["permits"] = permits.ToString(CultureInfo.InvariantCulture),
                    ["window_seconds"] = ((int)window.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                    ["subject"] = kind,
                    ["path"] = http.Request.Path.Value ?? string.Empty,
                };
                await audit.WriteAsync(new AuditEvent(ProtectionAuditEvents.RateLimited, AuditOutcome.Denied, principal, policy, details, Severity: AuditSeverity.Low), http.RequestAborted).ConfigureAwait(false);
            }
        }

        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        var error = new Error(ErrorKind.TooManyRequests, ProtectionErrors.RateLimited, $"Too many requests. Wait {seconds} seconds, then try again.")
        {
            Extensions = new Dictionary<string, object>(StringComparer.Ordinal) { ["retry_after_seconds"] = seconds, ["limit"] = policy },
        };
        await ApiResults.Problem(error, http).ExecuteAsync(http).ConfigureAwait(false);
    }
}
