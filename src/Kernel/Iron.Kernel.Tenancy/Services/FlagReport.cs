using Iron.Kernel.Api.Modules;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>A flag past its planned removal date, with whoever answers for it.</summary>
public sealed record OverdueFlag(string Key, string Module, string Owner, DateOnly RemoveBy);

/// <summary>Rules for a flag the catalogue no longer has, or has retired: they evaluate nothing and should go.</summary>
public sealed record OrphanRules(string Flag, int Rules, string Why);

/// <summary>The flag report (TF-052 AC1): flag debt to pay.</summary>
public sealed record FlagReportResult(DateOnly On, IReadOnlyList<OverdueFlag> PastRemoval, IReadOnlyList<OrphanRules> Orphans);

/// <summary>
/// The flag report (TF-052, ADR-0035): flags of this build past their removal date with their owner, and rules left for
/// flags the catalogue no longer has. The control plane logs it at start and answers it at <c>GET /api/v1/flags/report</c>;
/// the fleet worker logs it once a week.
/// </summary>
public sealed class FlagReportService(TenancyDbContext context, UnitCatalogue catalogue, TimeProvider time)
{
    public async Task<FlagReportResult> BuildAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var overdue = catalogue.Catalogue.Flags
            .Where(f => f.Status == FlagStatus.Active && f.RemoveBy is { } date && date < today)
            .Select(f => new OverdueFlag(f.Key, f.Module, f.Owner, f.RemoveBy!.Value))
            .ToList();
        var counts = await context.FlagRules.AsNoTracking().GroupBy(r => r.FlagKey).Select(g => new { Flag = g.Key, Rules = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var orphans = counts
            .Select(c => (c.Flag, c.Rules, Entry: catalogue.Catalogue.FindFlag(c.Flag)))
            .Where(c => c.Entry is not { Status: FlagStatus.Active })
            .Select(c => new OrphanRules(c.Flag, c.Rules, c.Entry is null ? "not in the catalogue" : "retired"))
            .OrderBy(o => o.Flag, StringComparer.Ordinal)
            .ToList();
        return new FlagReportResult(today, overdue, orphans);
    }
}

/// <summary>Logs the flag report: once at start (the control plane), or every week (the fleet worker).</summary>
public sealed class FlagReportLogger(IServiceScopeFactory scopes, TimeProvider time, ILogger<FlagReportLogger> logger, TimeSpan? every = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await LogAsync(stoppingToken).ConfigureAwait(false);
            if (every is not { } interval)
            {
                return;
            }

            using var timer = new PeriodicTimer(interval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await LogAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    public async Task LogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var report = await scope.ServiceProvider.GetRequiredService<FlagReportService>().BuildAsync(cancellationToken).ConfigureAwait(false);
            foreach (var flag in report.PastRemoval)
            {
                logger.LogWarning("Flag {Flag} of module {Module} is past its removal date {RemoveBy}; its owner is {Owner}.", flag.Key, flag.Module, flag.RemoveBy, flag.Owner);
            }

            foreach (var orphan in report.Orphans)
            {
                logger.LogWarning("{Rules} rule(s) remain for flag {Flag}, which is {Why}; remove them.", orphan.Rules, orphan.Flag, orphan.Why);
            }

            logger.LogInformation("Flag report: {Overdue} flag(s) past their removal date, {Orphans} flag(s) with rules left behind.", report.PastRemoval.Count, report.Orphans.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The flag report could not be built.");
        }
    }
}
