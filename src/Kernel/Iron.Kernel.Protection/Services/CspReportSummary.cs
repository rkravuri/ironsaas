using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Protection.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Protection.Services;

/// <summary>
/// Once a minute, the reports of the content security policy the process received since the last summary, as one audit
/// event at low severity (ADR-0056): how many, and the directives they broke. The reports come from anyone, so one event
/// each would let a stranger fill the trail; one a minute per process is bounded, and still lets an alert rule count the
/// reports, by the summary's <see cref="AuditDetails.Reports"/>.
/// </summary>
public sealed class CspReportSummary(IServiceScopeFactory scopes, CspReports reports, TimeProvider time, ILogger<CspReportSummary> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Writes what waits, if anything; answers how many reports it stood for.</summary>
    public async Task<int> SummariseAsync(CancellationToken cancellationToken)
    {
        var pending = reports.TakePending();
        var total = pending.Values.Sum();
        if (total == 0)
        {
            return 0;
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditDetails.Reports] = total.ToString(CultureInfo.InvariantCulture),
            ["directives"] = string.Join(',', pending.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(10).Select(p => $"{p.Key}={p.Value.ToString(CultureInfo.InvariantCulture)}")),
        };
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAuditWriter>()
            .WriteAsync(new AuditEvent(ProtectionAuditEvents.CspReportsReceived, AuditOutcome.Success, SystemPrincipal.SystemId, "content security policy", details, Severity: AuditSeverity.Low), cancellationToken)
            .ConfigureAwait(false);
        return total;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SummariseAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "The summary of the content security policy's reports could not be written; it is tried again in a minute.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}
