using Iron.Kernel.Flags.Contracts;
using Iron.Kernel.Flags.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Flags.Services;

/// <summary>
/// Reads the unit's flag rules every <see cref="FlagsOptions.RefreshInterval"/> (ADR-0035) from both paths and keeps the
/// last known rules in <c>kernel_flags.last_known</c>. Before the host serves, it binds the provider to OpenFeature,
/// restores the last known rules and reads the store once, within a few seconds. A failed read keeps what the unit
/// knows, however old; only a unit that has never known any rules runs on the code defaults, and that raises the alert:
/// an error in the log (once, then hourly), the <c>flags.unknown</c> metric, and a degraded readiness check.
/// </summary>
public sealed class FlagRefresher(
    IServiceScopeFactory scopes,
    IServiceProvider services,
    IFlagRuleSource source,
    FlagRulesCache cache,
    FlagsOptions options,
    FlagsMetrics metrics,
    TimeProvider time,
    ILogger<FlagRefresher> logger) : IHostedService
{
    /// <summary>How often a confirmation that changed nothing is written to the database.</summary>
    public static readonly TimeSpan PersistEvery = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan AlertEvery = TimeSpan.FromHours(1);

    private DateTimeOffset? _alertedAt;
    private DateTimeOffset? _persistedAt;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The provider first: a misconfigured one stops the host here, with its message (TF-056 AC2).
        services.GetRequiredService<OpenFeatureBinding>();

        using var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        first.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.StartTimeoutSeconds)));
        try
        {
            await RestoreAsync(first.Token).ConfigureAwait(false);
            await RefreshOnceAsync(first.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The first read of the flag rules took longer than {Seconds} s; the unit starts with what it has and reads again in {Interval}.", options.StartTimeoutSeconds, options.RefreshInterval);
        }

    }

    /// <summary>The reads after the first are the job <c>flags.refresh_cache</c>, which every process runs for itself (ADR-0061).</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The last known rules from the database, when this process has none fresher. A failure is logged, never thrown.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<FlagsDbContext>();
            var row = await context.LastKnown.AsNoTracking().SingleOrDefaultAsync(r => r.Key == LastKnownFlagRules.CurrentKey, cancellationToken).ConfigureAwait(false);
            if (row is not null && cache.Restore(new FlagRuleSet(Parse(row.PlatformDocument), Parse(row.TenantDocument)), row.ConfirmedAt))
            {
                logger.LogInformation("Restored the last known flag rules, confirmed at {ConfirmedAt:o}.", row.ConfirmedAt);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The last known flag rules could not be read from the database.");
        }
    }

    /// <summary>One read of both paths: new rules replace the old ones and are kept; a failure keeps the old ones.</summary>
    public async Task<bool> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        FlagRuleSet rules;
        try
        {
            rules = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Failed(exception, now);
            return false;
        }

        var previous = cache.Current;
        cache.Confirm(rules, now);
        if (_alertedAt is not null)
        {
            logger.LogInformation("The flag rules are known again, from {Source}.", source.Describe());
            _alertedAt = null;
        }

        var changed = previous is null || previous.Rules.Platform?.Revision != rules.Platform?.Revision || previous.Rules.Tenant?.Revision != rules.Tenant?.Revision;
        if (changed)
        {
            logger.LogInformation("Flag rules from {Source}: platform revision {Platform}, tenant revision {Tenant}, {Count} rule(s).", source.Describe(), rules.Platform?.Revision ?? 0, rules.Tenant?.Revision ?? 0, rules.Rules.Count());
        }

        if (changed || _persistedAt is null || now - _persistedAt >= PersistEvery)
        {
            await PersistAsync(rules, now, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static FlagRulesDocument? Parse(string? json) => json is null ? null : FlagRulesDocument.Parse(json);

    private void Failed(Exception exception, DateTimeOffset now)
    {
        metrics.RefreshFailed();
        cache.Fail(exception.Message, now);
        if (cache.Current is { } current)
        {
            logger.LogWarning("The flag rules could not be read from {Source} ({Error}); the rules confirmed at {ConfirmedAt:o} stand.", source.Describe(), exception.Message, current.ConfirmedAt);
            return;
        }

        if (_alertedAt is null || now - _alertedAt >= AlertEvery)
        {
            _alertedAt = now;
            logger.LogError(
                exception,
                "ALERT: no flag rules are known: {Source} has not answered and the unit never knew any rules, so every flag has its code default and no kill switch can be on. Check the store and the unit's token (make update-unit renews its policy).",
                source.Describe());
        }
    }

    private async Task PersistAsync(FlagRuleSet rules, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<FlagsDbContext>();
            var by = scope.ServiceProvider.GetRequiredService<ICurrentPrincipal>().Id;
            var platform = rules.Platform?.ToJson();
            var tenant = rules.Tenant?.ToJson();

            // One statement: the API and the worker keep the same row and may both write it at once on a unit's first start,
            // and the second then updates what the first inserted instead of failing on the unique key. The audit columns
            // are set here as the save interceptor would set them.
            await context.Database.ExecuteSqlAsync(
                $"""
                insert into kernel_flags.last_known (id, key, platform_document, tenant_document, confirmed_at, created_at, created_by, updated_at, updated_by, version)
                values ({Ids.New()}, {LastKnownFlagRules.CurrentKey}, cast({platform} as jsonb), cast({tenant} as jsonb), {now}, {now}, {by}, {now}, {by}, 1)
                on conflict (key) do update set platform_document = excluded.platform_document, tenant_document = excluded.tenant_document,
                    confirmed_at = excluded.confirmed_at, updated_at = excluded.updated_at, updated_by = excluded.updated_by, version = last_known.version + 1
                """,
                cancellationToken).ConfigureAwait(false);
            _persistedAt = now;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The database did not answer; the rules in memory stand, and the next read keeps them again.
            logger.LogWarning("The last known flag rules could not be kept in the database: {Error}", exception.Message);
        }
    }
}

/// <summary>
/// <c>flags.refresh_cache</c> (TF-124, ADR-0035, ADR-0061): every <c>Flags:RefreshSeconds</c> (30 s), each process reads
/// the flag rules into its own cache, which no other process can do for it, so it runs in each process. The first read is
/// the refresher's, before the host serves.
/// </summary>
public sealed class FlagRefreshJob(FlagRefresher refresher) : IJob
{
    public static readonly JobDefinition Definition = JobDefinition.Create<FlagRefreshJob>(
        "flags.refresh_cache", "Reads the flag rules into this process's cache.", "Platform team",
        JobSchedule.Interval(TimeSpan.FromSeconds(30), firstAfter: TimeSpan.FromSeconds(30), configurationKey: $"{FlagsOptions.SectionName}:RefreshSeconds", longestConfigured: TimeSpan.FromSeconds(60)),
        PriorityClass.Normal, TimeSpan.FromSeconds(30), new RetryPolicy(1, TimeSpan.Zero, TimeSpan.Zero), JobScope.EachProcess);

    /// <summary>A failed read keeps what the process knows and raises its own alert; the next interval reads again.</summary>
    public async Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        await refresher.RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
