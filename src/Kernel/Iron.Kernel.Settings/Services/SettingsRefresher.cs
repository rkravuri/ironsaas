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
public sealed class SettingsRefresher(IServiceScopeFactory scopes, SettingsCache cache, SettingsOptions options, SettingsMetrics metrics, TimeProvider time, ILogger<SettingsRefresher> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan AlertEvery = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _alertedAt;

    public async Task StartAsync(CancellationToken cancellationToken)
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

    }

    /// <summary>The reads after the first are the job <c>settings.refresh_cache</c>, which every process runs for itself (ADR-0061).</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

    public void Dispose() => _gate.Dispose();

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

/// <summary>
/// <c>settings.refresh_cache</c> (TF-124, ADR-0036, ADR-0061): every <c>Settings:RefreshSeconds</c> (30 s), each process
/// reads every setting's value into its own cache, so it runs in each process. The first read is the refresher's.
/// </summary>
public sealed class SettingsRefreshJob(SettingsRefresher refresher) : IJob
{
    public static readonly JobDefinition Definition = JobDefinition.Create<SettingsRefreshJob>(
        "settings.refresh_cache", "Reads every setting's value into this process's cache.", "Platform team",
        JobSchedule.Interval(TimeSpan.FromSeconds(30), firstAfter: TimeSpan.FromSeconds(30), configurationKey: $"{SettingsOptions.SectionName}:RefreshSeconds", longestConfigured: TimeSpan.FromSeconds(30)),
        PriorityClass.Normal, TimeSpan.FromSeconds(30), new RetryPolicy(1, TimeSpan.Zero, TimeSpan.Zero), JobScope.EachProcess);

    /// <summary>A failed read keeps the last values and raises its own alert; the next interval reads again.</summary>
    public async Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        await refresher.RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
