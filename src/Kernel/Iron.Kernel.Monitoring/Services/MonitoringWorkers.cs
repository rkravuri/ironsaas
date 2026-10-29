using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Monitoring.Services;

/// <summary>A pass at once, then one each interval, each in a scope of its own; a pass that throws is logged and the next one runs.</summary>
public abstract class MonitoringLoop(IServiceScopeFactory scopes, TimeProvider time, ILogger logger) : BackgroundService
{
    protected abstract TimeSpan Interval { get; }

    protected abstract string What { get; }

    protected abstract Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await RunOnceAsync(scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "The {What} failed; it runs again in {Interval}.", What, Interval);
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

/// <summary>The rules, every minute (TF-079).</summary>
public sealed class RuleEvaluationWorker(IServiceScopeFactory scopes, MonitoringOptions options, TimeProvider time, ILogger<RuleEvaluationWorker> logger)
    : MonitoringLoop(scopes, time, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(1, options.EvaluationSeconds));

    protected override string What => "pass of the alert rules";

    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<RuleEvaluator>().RunAsync(cancellationToken);
}

/// <summary>A unit's changed alerts to the control plane.</summary>
public sealed class AlertCopyWorker(IServiceScopeFactory scopes, MonitoringOptions options, TimeProvider time, ILogger<AlertCopyWorker> logger)
    : MonitoringLoop(scopes, time, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(1, options.CopySeconds));

    protected override string What => "copy of the alerts to the control plane";

    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<AlertCopySender>().SendAsync(cancellationToken);
}

/// <summary>The control plane's alerts to the operator's alerting.</summary>
public sealed class AlertmanagerWorker(IServiceScopeFactory scopes, MonitoringOptions options, TimeProvider time, ILogger<AlertmanagerWorker> logger)
    : MonitoringLoop(scopes, time, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(1, options.Alertmanager.Seconds));

    protected override string What => "send of the alerts to the operator's alerting";

    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<AlertmanagerForwarder>().SendAsync(cancellationToken);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Alertmanager.Address))
        {
            logger.LogWarning("No operator's alerting is configured (Monitoring:Alertmanager:Address): the security alerts stay in the control plane.");
        }

        return base.StartAsync(cancellationToken);
    }
}
