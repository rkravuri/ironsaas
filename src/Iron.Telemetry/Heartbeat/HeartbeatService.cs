using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Telemetry.Heartbeat;

/// <summary>Activity sources of Foundation's own spans. Every source named Iron.* is traced.</summary>
public static class IronActivitySources
{
    public static readonly ActivitySource Worker = new("Iron.Worker");
}

public sealed class HeartbeatOptions
{
    public const string SectionName = "Heartbeat";

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// A worker's sign of life: on start and then at a fixed interval it records a <c>worker.heartbeat</c> span and a log
/// line carrying the tenant, so the fleet and the dashboards see that every unit's worker runs (TF-003, TF-130).
/// </summary>
public sealed class HeartbeatService(ILogger<HeartbeatService> logger, TelemetryOptions telemetry, HeartbeatOptions options, TimeProvider time) : BackgroundService
{
    public const string ActivityName = "worker.heartbeat";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval, time);
        try
        {
            do
            {
                Beat();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Stopping: nothing to do.
        }
    }

    private void Beat()
    {
        var tenant = telemetry.TenantId ?? "unbound";

        // A heartbeat is its own trace: it must not hang off whatever activity is current on this thread, and it must
        // not inherit that activity's sampling decision.
        var ambient = Activity.Current;
        Activity.Current = null;
        try
        {
            using var activity = IronActivitySources.Worker.StartActivity(ActivityName);
            activity?.SetTag("tenant.id", tenant);
            logger.LogInformation("Heartbeat from {Service} for tenant {TenantId}.", telemetry.ServiceName, tenant);
        }
        finally
        {
            Activity.Current = ambient;
        }
    }
}

public static class HeartbeatExtensions
{
    /// <summary>Binds <c>Heartbeat:Interval</c> from configuration; code can adjust it afterwards.</summary>
    public static IServiceCollection AddIronHeartbeat(this IServiceCollection services, IConfiguration configuration, Action<HeartbeatOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new HeartbeatOptions();
        configuration.GetSection(HeartbeatOptions.SectionName).Bind(options);
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<HeartbeatService>();
        return services;
    }
}
