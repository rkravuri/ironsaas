using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Fleet.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Fleet.Services;

/// <summary>The fleet worker's loop: every few seconds, the oldest Requested job runs to its end. One at a time; one fleet in S1.</summary>
public sealed class ProvisioningWorker(IServiceScopeFactory scopes, FleetOptions options, TimeProvider time, ILogger<ProvisioningWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("The fleet worker is up; it looks for provisioning jobs every {Seconds} s.", options.PollSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), time);
        try
        {
            do
            {
                await RunNextAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>Runs the oldest Requested job, if any. Errors are logged, never thrown: a database blip must not stop the worker.</summary>
    public async Task<bool> RunNextAsync(CancellationToken cancellationToken)
    {
        Guid? jobId = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            var next = await context.ProvisioningJobs.AsNoTracking()
                .Where(j => j.Status == ProvisioningStatus.Requested)
                .OrderBy(j => j.RequestedAt)
                .ThenBy(j => j.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (next is null)
            {
                return false;
            }

            jobId = next.Id;
            await scope.ServiceProvider.GetRequiredService<ProvisioningRunner>().RunAsync(next.Id, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (jobId is null)
            {
                logger.LogError(exception, "The fleet worker could not look for provisioning jobs.");
            }
            else
            {
                logger.LogError(exception, "Provisioning job {JobId} ended with an unexpected error.", jobId);
            }

            return jobId is not null;
        }
    }
}
