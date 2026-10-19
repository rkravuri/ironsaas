using System.Text.Json;
using Iron.Kernel.Settings.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Settings.Services;

/// <summary>
/// Reads every value of the tenant into the cache (ADR-0036): once before the host serves, then every 30 seconds, and at
/// once after a change made in this process, so the process that saved a value answers it on the next request and every
/// other process within 30 seconds, without a restart (TF-059 AC2). A failed read keeps the last values; after five
/// minutes of failures the alert is raised (TF-059 AC4): an error line, the gauge, and the readiness detail.
/// </summary>
public sealed class SettingsRefresher(IServiceScopeFactory scopes, SettingsCache cache, SettingsOptions options, SettingsMetrics metrics, TimeProvider time, ILogger<SettingsRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan AlertEvery = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _alertedAt;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        first.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.StartTimeoutSeconds)));
        try
        {
            await RefreshOnceAsync(first.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The first read of the settings took longer than {Seconds} s; the host starts on the defaults and reads again in {Interval}.", options.StartTimeoutSeconds, options.RefreshInterval);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.RefreshInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>One read of every value. A failure keeps the last values and is logged, never thrown.</summary>
    public async Task<bool> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<SettingsDbContext>();
                var rows = await context.Values.AsNoTracking()
                    .Select(v => new { v.Key, v.Level, v.TargetId, v.ValueJson, v.UpdatedBy, v.UpdatedAt })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                var values = rows.Select(r => new StoredValue(r.Key, r.Level, r.TargetId, Parse(r.ValueJson), r.UpdatedBy, r.UpdatedAt));
                cache.Replace(new SettingsSnapshot(values, now));
                if (_alertedAt is not null)
                {
                    logger.LogInformation("The settings refresh again.");
                    _alertedAt = null;
                }

                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Failed(exception, now);
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private void Failed(Exception exception, DateTimeOffset now)
    {
        metrics.RefreshFailed();
        cache.Fail(exception.Message, now);
        var since = cache.Failing?.Since ?? now;
        var stand = cache.Current is { } current ? $"the values read at {current.ReadAt:o} stand" : "every setting has its default";
        if (now - since >= options.AlertAfter && (_alertedAt is null || now - _alertedAt >= AlertEvery))
        {
            _alertedAt = now;
            logger.LogError(exception, "ALERT: the settings have not refreshed since {Since:o}, so changes made since then are not seen here; {Stand}. Check the database.", since, stand);
            return;
        }

        logger.LogWarning("The settings could not be read ({Error}); {Stand}.", exception.Message, stand);
    }
}
