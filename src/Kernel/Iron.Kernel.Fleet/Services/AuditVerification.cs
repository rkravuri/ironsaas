using System.Collections.Concurrent;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Fleet.Services.Steps;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iron.Kernel.Fleet.Services;

/// <summary>
/// The fleet's verification of a tenant's audit chain (TF-061, ADR-0050). The fleet, not the unit, reads the chain, with
/// the tenant's verifier role, which can read the chain's tables and nothing else, and compares it with the anchors the
/// control plane keeps outside the tenant's database. A daily run checks what was sealed since the last verification
/// that passed; a full run, once a month, after a failure, or when an operator asks, checks from the oldest position
/// still kept. The archived part of the chain is read from the tenant's container.
/// </summary>
public sealed class AuditChainVerifier(ITenantAuditChains chains, ISecretStore secrets, FleetOptions options, ArchivedChainReader archive, TimeProvider time)
{
    public async Task<AuditVerificationInfo> VerifyAsync(AuditVerificationDue due, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(due);
        var anchors = (await chains.AnchorsAsync(due.TenantSlug, cancellationToken).ConfigureAwait(false))
            .Select(a => new AuditAnchor(a.Kind, a.Position, a.Hash, a.At))
            .ToList();

        // The oldest end: everything before the latest pruned anchor was removed after its retention, which that anchor's
        // hash stands for (ADR-0050).
        var start = 1L;
        var previous = AuditCanonicalForm.Genesis;
        if (anchors.Where(a => a.Kind == AuditAnchorKinds.Pruned).MaxBy(a => a.Position) is { } pruned)
        {
            start = pruned.Position + 1;
            previous = pruned.Hash;
        }

        var full = due.Full;
        if (!full)
        {
            var last = await chains.LatestVerificationAsync(due.TenantSlug, false, cancellationToken).ConfigureAwait(false);
            if (last is { Result: AuditVerificationResults.Passed, ToHash: { } verifiedHash } && last.ToPosition >= start - 1)
            {
                start = last.ToPosition + 1;
                previous = verifiedHash;
            }
            else
            {
                full = true;
            }
        }

        var result = await CheckAsync(due, start, previous, anchors, cancellationToken).ConfigureAwait(false);
        if (!full && result.Restored)
        {
            // A restore to before the last verification: only the whole chain shows where it ends.
            full = true;
            start = anchors.Where(a => a.Kind == AuditAnchorKinds.Pruned).MaxBy(a => a.Position) is { } oldest ? oldest.Position + 1 : 1;
            previous = anchors.Where(a => a.Kind == AuditAnchorKinds.Pruned).MaxBy(a => a.Position)?.Hash ?? AuditCanonicalForm.Genesis;
            result = await CheckAsync(due, start, previous, anchors, cancellationToken).ConfigureAwait(false);
        }

        return new AuditVerificationInfo(
            due.TenantSlug,
            null,
            full,
            result.FromPosition,
            result.ToPosition,
            result.ToHash,
            ResultOf(result),
            result.EventsChecked,
            [.. result.Problems.Select(p => new AuditChainProblemInfo(p.Kind, p.Position, p.EventId, p.Message))],
            result.RestorePosition,
            result.RestoreAt,
            time.GetUtcNow());
    }

    /// <summary>
    /// Restored when the only disagreement is the chain ending before its anchors, or the anchors after the restore point;
    /// failed for anything else, a missing or changed event above all.
    /// </summary>
    public static string ResultOf(ChainCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Passed)
        {
            return AuditVerificationResults.Passed;
        }

        return result.RestorePosition is { } point && result.Problems.All(p => p.Kind == ChainProblemKinds.AnchorMismatch && p.Position > point)
            ? AuditVerificationResults.Restored
            : AuditVerificationResults.Failed;
    }

    private async Task<ChainCheckResult> CheckAsync(AuditVerificationDue due, long start, string previous, IReadOnlyList<AuditAnchor> anchors, CancellationToken cancellationToken)
    {
        await using var dataSource = await ReaderAsync(due, cancellationToken).ConfigureAwait(false);
        var events = new List<SealedEvent>();
        events.AddRange(await archive.ReadAsync(due.StorageContainer, dataSource, start, cancellationToken).ConfigureAwait(false));
        var online = await ReadOnlineAsync(dataSource, start, cancellationToken).ConfigureAwait(false);
        var archivedUpTo = events.Count == 0 ? 0 : events.Max(e => e.Position);
        events.AddRange(online.Where(e => e.Position > archivedUpTo));
        return AuditChainCheck.Check(start, previous, events, anchors);
    }

    /// <summary>The tenant's database as its verifier role reaches it, on the fleet's own route to the server.</summary>
    private async Task<NpgsqlDataSource> ReaderAsync(AuditVerificationDue due, CancellationToken cancellationToken)
    {
        var admin = options.Database.AdminConnectionString ?? throw new InvalidOperationException("Fleet:Database:AdminConnectionString is required to verify the tenants' audit chains.");
        var database = await secrets.ReadAsync($"tenants/{due.TenantSlug}/database", cancellationToken).ConfigureAwait(false);
        var password = database?.GetValueOrDefault(DatabaseStep.AuditPasswordKey)
            ?? throw new InvalidOperationException($"Tenant {due.TenantSlug} has no audit verifier role yet: make update-unit TENANT={due.TenantSlug} makes it.");
        var builder = new NpgsqlConnectionStringBuilder(admin)
        {
            Database = due.DatabaseName,
            Username = $"{due.TenantSlug}_audit",
            Password = password,
            Pooling = false,
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    /// <summary>The sealed events still in the database from a position on, in chain order.</summary>
    public static async Task<IReadOnlyList<SealedEvent>> ReadOnlineAsync(NpgsqlDataSource dataSource, long start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var command = dataSource.CreateCommand(
            "select id, chain_position, created_at, created_by, event_type, outcome, actor_id, on_behalf_of_id, impersonator_id, source, severity, " +
            "target, record_type, record_id, reason, correlation_id, details::text, previous_hash, hash, hash_version, sealed_at " +
            "from kernel_audit.events where chain_position >= @start order by chain_position");
        command.Parameters.AddWithValue("start", start);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var events = new List<SealedEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new SealedEvent(
                new ChainedEvent(
                    reader.GetGuid(0),
                    reader.GetInt64(1),
                    reader.GetFieldValue<DateTimeOffset>(2),
                    reader.GetGuid(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetGuid(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    reader.IsDBNull(8) ? null : reader.GetGuid(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetGuid(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15),
                    reader.IsDBNull(16) ? null : reader.GetString(16)),
                reader.GetString(17),
                reader.GetString(18),
                reader.GetInt16(19),
                reader.GetFieldValue<DateTimeOffset>(20)));
        }

        return events;
    }
}

/// <summary>
/// The fleet's minute loop for the chains (TF-061): it asks the control plane what is due, verifies each, and records the
/// result there, which raises the alert when one did not pass. A tenant it could not reach is tried again an hour later,
/// so a unit made before the chain says so once an hour in the log until <c>make update-unit</c> gives it its role.
/// </summary>
public sealed class AuditVerificationWorker(IServiceScopeFactory scopes, FleetOptions options, TimeProvider time, ILogger<AuditVerificationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _unreachable = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Audit.PollSeconds)), time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The audit verification pass failed; it runs again in {Seconds} seconds.", options.Audit.PollSeconds);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One pass: every verification due now.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var chains = scope.ServiceProvider.GetRequiredService<ITenantAuditChains>();
        var verifier = scope.ServiceProvider.GetRequiredService<AuditChainVerifier>();
        var now = time.GetUtcNow();
        var done = 0;
        foreach (var due in await chains.DueAsync(now, cancellationToken).ConfigureAwait(false))
        {
            if (due.RequestId is null && _unreachable.TryGetValue(due.TenantSlug, out var failedAt) && now - failedAt < RetryAfter)
            {
                continue;
            }

            try
            {
                var verification = await verifier.VerifyAsync(due, cancellationToken).ConfigureAwait(false);
                await chains.RecordVerificationAsync(verification, due.RequestId, cancellationToken).ConfigureAwait(false);
                _unreachable.TryRemove(due.TenantSlug, out _);
                done++;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NpgsqlException)
            {
                _unreachable[due.TenantSlug] = now;
                logger.LogWarning("The audit chain of tenant {Slug} could not be read: {Message}", due.TenantSlug, exception.Message);
            }
        }

        return done;
    }
}

/// <summary>
/// The archived part of a tenant's chain (TF-064 AC2, ADR-0050), as the verifier reads it: the archive index from the
/// tenant's database with the verifier role, each batch from the tenant's container with the fleet's own account. A batch
/// changed in the container shows in the check as changed or missing events; one the container no longer has leaves its
/// positions missing.
/// </summary>
public sealed class ArchivedChainReader(FleetOptions options)
{
    public async Task<IReadOnlyList<SealedEvent>> ReadAsync(string containerName, NpgsqlDataSource dataSource, long start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var batches = new List<(long First, long Last, string Key)>();
        await using (var command = dataSource.CreateCommand("select first_position, last_position, blob_key from kernel_audit.archive_batches where last_position >= @start order by first_position"))
        {
            command.Parameters.AddWithValue("start", start);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                batches.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
            }
        }

        if (batches.Count == 0)
        {
            return [];
        }

        var account = options.Storage.AccountConnectionString ?? throw new InvalidOperationException("Fleet:Storage:AccountConnectionString is required to read the tenants' audit archives.");
        var container = new Azure.Storage.Blobs.BlobContainerClient(account, containerName);
        var events = new List<SealedEvent>();
        foreach (var batch in batches)
        {
            var blob = container.GetBlobClient(batch.Key);
            if (!await blob.ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var content = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            events.AddRange(AuditArchiveFormat.Read(content.Value.Content.ToMemory().Span).Where(e => e.Position >= start));
        }

        return events;
    }
}
