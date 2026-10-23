using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Changes.Services;

/// <summary>
/// Ends the pending changes nobody decided within the tenant's period (ADR-0052), every five minutes, in the unit's
/// worker. The lists and the decisions expire a change past its time as well, so a late pass never lets one apply.
/// </summary>
public sealed class ChangeExpiryWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<ChangeExpiryWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<PendingChanges>().ExpireAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "The pass that expires configuration changes failed; it runs again in {Interval}.", Interval);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}
