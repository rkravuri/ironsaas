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

/// <summary>
/// <c>monitoring.evaluate_rules</c> (TF-079, TF-124, ADR-0056, ADR-0061): the alert rules over the unit's trail, every minute
/// (<c>Monitoring:EvaluationSeconds</c>). A pass takes a lock of its own, and a rule that counts each event catches up
/// after a gap, so a repeated run raises nothing twice.
/// </summary>
public sealed class RuleEvaluationJob(RuleEvaluator evaluator) : IJob
{
    public static readonly JobDefinition Definition = JobDefinition.Create<RuleEvaluationJob>(
        "monitoring.evaluate_rules", "Runs the security alert rules over the tenant's audit trail every minute.", "Platform security",
        JobSchedule.Interval(TimeSpan.FromMinutes(1), configurationKey: "Monitoring:EvaluationSeconds"), PriorityClass.Normal, TimeSpan.FromMinutes(5));

    public async Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        await evaluator.RunAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}

/// <summary>
/// <c>monitoring.copy_alerts</c> (TF-079, ADR-0056, ADR-0061): every <c>Monitoring:CopySeconds</c> (15 s), the unit's
/// changed alerts to the control plane, as a run a minute that passes; the control plane keeps only the newest revision.
/// </summary>
public sealed class AlertCopyJob(IServiceScopeFactory scopes, MonitoringOptions options, TimeProvider time) : IJob
{
    public static readonly JobDefinition Definition = JobDefinition.Create<AlertCopyJob>(
        "monitoring.copy_alerts", "Sends the tenant's changed security alerts to the control plane every few seconds.", "Platform security",
        JobSchedule.Interval(TimeSpan.FromMinutes(1)), PriorityClass.Normal, TimeSpan.FromMinutes(2));

    public Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken) =>
        JobLoop.RunAsync(context, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(Math.Max(1, options.CopySeconds)), time, async ct =>
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AlertCopySender>().SendAsync(ct).ConfigureAwait(false);
            return false;
        }, cancellationToken);
}

/// <summary>The control plane's rules, every minute (TF-079), in its API; a unit runs them as its job.</summary>
public sealed class RuleEvaluationWorker(IServiceScopeFactory scopes, MonitoringOptions options, TimeProvider time, ILogger<RuleEvaluationWorker> logger)
    : MonitoringLoop(scopes, time, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(1, options.EvaluationSeconds));

    protected override string What => "pass of the alert rules";

    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<RuleEvaluator>().RunAsync(cancellationToken);
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
