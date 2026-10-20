using Iron.Kernel.Authorization.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// Runs the conflicts report in the unit's worker (TF-036 AC3): two minutes after the worker starts, so a restart
/// catches up, then once a day, so a rule written or changed shows its conflicts within a day. A failed run is logged
/// and tried again the next day; nothing is lost, since a run looks at the rights as they are.
/// </summary>
public sealed class ConflictReportWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<ConflictReportWorker> logger) : BackgroundService
{
    public static readonly TimeSpan FirstRunAfter = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunAfter, time, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var run = await scope.ServiceProvider.GetRequiredService<ConflictService>().RunReportAsync(ConflictReportTriggers.Daily, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("The conflicts report ran: {Rules} rule(s), {Conflicts} conflict(s).", run.Rules, run.Conflicts);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The conflicts report failed; it runs again in {Interval}.", Interval);
        }
    }
}
