using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Entitlements.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Entitlements.Services;

/// <summary>
/// Reads the unit's entitlements every <see cref="EntitlementsOptions.RefreshInterval"/> (TF-043, ADR-0034) and keeps
/// the last known answer in <c>kernel_entitlements.last_known</c>, so a restart while the store is down still knows.
/// Before the host serves, it restores that answer and reads the store once, within a few seconds. When the answer is
/// older than 24 hours, or there never was one, it raises the alert: an error in the log (once, then hourly), the
/// <c>entitlements.stale</c> metric, and a degraded readiness check. Nothing it meets stops the host.
/// </summary>
public sealed class EntitlementRefresher(
    IServiceScopeFactory scopes,
    IEntitlementSource source,
    EntitlementCache cache,
    EntitlementsOptions options,
    EntitlementsMetrics metrics,
    TimeProvider time,
    ILogger<EntitlementRefresher> logger,
    TenantContext? tenant = null) : BackgroundService
{
    /// <summary>How often a confirmation that changed nothing is written to the database: the age a restart sees is at most this wrong.</summary>
    public static readonly TimeSpan PersistEvery = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan AlertEvery = TimeSpan.FromHours(1);

    private DateTimeOffset? _alertedAt;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        first.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.StartTimeoutSeconds)));
        try
        {
            await RestoreAsync(first.Token).ConfigureAwait(false);
            await RefreshOnceAsync(first.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The first read of the entitlements took longer than {Seconds} s; the unit starts with what it has and reads again in {Interval}.", options.StartTimeoutSeconds, options.RefreshInterval);
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

    /// <summary>The last known answer from the database, when this process has none fresher. A failure is logged, never thrown.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<EntitlementsDbContext>();
            var row = await context.LastKnown.AsNoTracking().SingleOrDefaultAsync(r => r.Key == LastKnownEntitlements.CurrentKey, cancellationToken).ConfigureAwait(false);
            if (row is not null && cache.Restore(EntitlementsDocument.Parse(row.Document), row.ConfirmedAt))
            {
                logger.LogInformation("Restored the last known entitlements, revision {Revision}, confirmed at {ConfirmedAt:o}.", row.Revision, row.ConfirmedAt);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The last known entitlements could not be read from the database.");
        }
    }

    /// <summary>One read of the source: a new answer replaces the old one and is kept; a failure keeps the old one.</summary>
    public async Task<bool> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        EntitlementsDocument document;
        try
        {
            document = await source.ReadAsync(cancellationToken).ConfigureAwait(false) ?? EntitlementsDocument.Empty(tenant?.Id.Value ?? "local", now);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Failed(exception, now);
            return false;
        }

        var previous = cache.Current;
        cache.Confirm(document, now);
        if (_alertedAt is not null)
        {
            logger.LogInformation("The entitlements are confirmed again by {Source}, revision {Revision}.", source.Describe(), document.Revision);
            _alertedAt = null;
        }
        else if (previous is null || previous.Document.Revision != document.Revision)
        {
            logger.LogInformation("Entitlements revision {Revision} from {Source}: {Modules} licensed module(s), {Features} feature(s).", document.Revision, source.Describe(), document.Modules.Count, document.Features.Count);
        }

        await PersistAsync(document, now, previous, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void Failed(Exception exception, DateTimeOffset now)
    {
        metrics.RefreshFailed();
        cache.Fail(exception.Message, now);
        var current = cache.Current;
        if (cache.Usable(now, options.StaleAfter) is not null)
        {
            logger.LogWarning("The entitlements could not be read from {Source} ({Error}); the answer confirmed at {ConfirmedAt:o} stands until {Until:o}.", source.Describe(), exception.Message, current!.ConfirmedAt, current.ConfirmedAt + options.StaleAfter);
            return;
        }

        if (_alertedAt is null || now - _alertedAt >= AlertEvery)
        {
            _alertedAt = now;
            logger.LogError(
                exception,
                "ALERT: the entitlements are unknown: {Source} has not answered and the last known answer is {Age}. Every licensed module and feature is treated as disabled until the store answers. Check the store and the unit's token (make update-unit renews its policy).",
                source.Describe(),
                current is null ? "missing" : $"from {current.ConfirmedAt:o}, older than {options.StaleAfterHours} hours");
        }
    }

    private async Task PersistAsync(EntitlementsDocument document, DateTimeOffset now, EntitlementSnapshot? previous, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<EntitlementsDbContext>();
            var by = scope.ServiceProvider.GetRequiredService<ICurrentPrincipal>().Id;
            var json = document.ToJson();
            var changed = previous?.Document.Revision != document.Revision;

            // One statement: the API and the worker keep the same row and may both write it at once on a unit's first start,
            // and the second then updates what the first inserted instead of failing on the unique key. An existing row is
            // written when the revision changed, and otherwise at most every ten minutes. The audit columns are set here
            // as the save interceptor would set them.
            await context.Database.ExecuteSqlAsync(
                $"""
                insert into kernel_entitlements.last_known (id, key, document, revision, confirmed_at, created_at, created_by, updated_at, updated_by, version)
                values ({Ids.New()}, {LastKnownEntitlements.CurrentKey}, cast({json} as jsonb), {document.Revision}, {now}, {now}, {by}, {now}, {by}, 1)
                on conflict (key) do update set document = excluded.document, revision = excluded.revision, confirmed_at = excluded.confirmed_at,
                    updated_at = excluded.updated_at, updated_by = excluded.updated_by, version = last_known.version + 1
                where {changed} or last_known.revision <> excluded.revision or excluded.confirmed_at - last_known.confirmed_at >= {PersistEvery}
                """,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The database did not answer; the answer in memory stands, and the next read keeps it again.
            logger.LogWarning("The last known entitlements could not be kept in the database: {Error}", exception.Message);
        }
    }
}
