using System.Globalization;
using System.Text;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Data.Retention;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Files.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Audit.Services;

/// <summary>
/// Moves the audit trail's oldest sealed events to the archive after the online period (TF-064 AC2, ADR-0050): in batches
/// of consecutive positions, each written whole with its chain columns to the tenant's container, indexed by position
/// and date range, and only then removed from the database through <c>kernel_audit.remove_archived</c>, the one way an
/// audit row ever leaves it. The search and the verification read the archive as they read the database.
/// </summary>
public sealed class AuditArchiver(AuditDbContext context, IObjectStorage storage, IUnitOfWork unitOfWork, IRetentionPeriods periods, IAuditWriter audit, TimeProvider time)
{
    public const int BatchSize = 1000;

    /// <summary>Archives every sealed event older than the online period; answers how many.</summary>
    public async Task<int> ArchiveAsync(CancellationToken cancellationToken)
    {
        var days = await periods.DaysAsync(RetentionClasses.AuditOnline, cancellationToken).ConfigureAwait(false);
        var cutoff = time.GetUtcNow().AddDays(-days);
        var total = 0;
        while (true)
        {
            var oldest = await context.Events.AsNoTracking()
                .Where(e => e.ChainPosition != null)
                .OrderBy(e => e.ChainPosition)
                .Take(BatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var batch = oldest.TakeWhile(e => e.CreatedAt < cutoff).ToList();
            if (batch.Count == 0)
            {
                return total;
            }

            var sealedEvents = batch.Select(e => new SealedEvent(e.Chained(e.ChainPosition!.Value), e.PreviousHash!, e.Hash!, e.HashVersion!.Value, e.SealedAt!.Value)).ToList();
            var bytes = AuditArchiveFormat.Write(sealedEvents);
            var first = sealedEvents[0];
            var last = sealedEvents[^1];
            var key = AuditArchiveFormat.KeyOf(first.Position, last.Position);
            await using (var stream = new MemoryStream(bytes))
            {
                await storage.PutAsync(key, stream, AuditArchiveFormat.ContentType, cancellationToken).ConfigureAwait(false);
            }

            await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
            context.ArchiveBatches.Add(new AuditArchiveBatch(first.Position, last.Position, sealedEvents.Min(e => e.Content.CreatedAt), sealedEvents.Max(e => e.Content.CreatedAt), sealedEvents.Count, key, AuditArchiveFormat.Sha256(bytes), last.Hash));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var removed = await context.Database.SqlQuery<int>($"select kernel_audit.remove_archived({last.Position}) as \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["first_position"] = first.Position.ToString(CultureInfo.InvariantCulture),
                ["last_position"] = last.Position.ToString(CultureInfo.InvariantCulture),
                ["events"] = removed.ToString(CultureInfo.InvariantCulture),
                ["key"] = key,
            };
            await audit.WriteAsync(new AuditEvent(AuditEvents.TrailArchived, AuditOutcome.Success, SystemPrincipal.SystemId, key, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            total += removed;
            if (batch.Count < oldest.Count || oldest.Count < BatchSize)
            {
                return total;
            }
        }
    }
}

/// <summary>
/// Removes the audit trail's oldest end after its retention (TF-064, ADR-0050): archived batches past the period, from the
/// oldest, never one with an event of a held record, and none at all while the whole tenant is held. What stays starts
/// from the last removed position's hash, which a pruning record keeps and the unit anchors, so the chain still verifies.
/// </summary>
public sealed class AuditPruner(AuditDbContext context, IObjectStorage storage, IUnitOfWork unitOfWork, IRetentionPeriods periods, ILegalHolds holds, IAuditWriter audit, TimeProvider time)
{
    public async Task<AuditPrune?> PruneAsync(CancellationToken cancellationToken)
    {
        var held = await holds.ActiveAsync(cancellationToken).ConfigureAwait(false);
        if (held.Tenant)
        {
            return null;
        }

        var days = await periods.DaysAsync(RetentionClasses.AuditEvent, cancellationToken).ConfigureAwait(false);
        var cutoff = time.GetUtcNow().AddDays(-days);
        var prunedUpTo = await context.Prunes.AsNoTracking().MaxAsync(p => (long?)p.UpToPosition, cancellationToken).ConfigureAwait(false) ?? 0;
        var candidates = await context.ArchiveBatches.AsNoTracking()
            .Where(b => b.LastPosition > prunedUpTo && b.LastAt < cutoff)
            .OrderBy(b => b.FirstPosition)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var removed = new List<AuditArchiveBatch>();
        foreach (var batch in candidates)
        {
            if (held.Records.Count + held.Masters.Count > 0 && await HoldsAnEventOfAsync(batch, held, cancellationToken).ConfigureAwait(false))
            {
                break; // from the oldest end only: a held batch keeps every one after it too
            }

            await storage.DeleteAsync(batch.BlobKey, cancellationToken).ConfigureAwait(false);
            removed.Add(batch);
        }

        if (removed.Count == 0)
        {
            return null;
        }

        var last = removed[^1];
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var prune = new AuditPrune(last.LastPosition, last.LastHash, removed.Count);
        context.Prunes.Add(prune);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["up_to_position"] = last.LastPosition.ToString(CultureInfo.InvariantCulture),
            ["hash"] = last.LastHash,
            ["batches"] = removed.Count.ToString(CultureInfo.InvariantCulture),
            ["days"] = days.ToString(CultureInfo.InvariantCulture),
        };
        await audit.WriteAsync(new AuditEvent(AuditEvents.TrailPruned, AuditOutcome.Success, SystemPrincipal.SystemId, "audit trail", details, Reason: "retention"), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return prune;
    }

    private async Task<bool> HoldsAnEventOfAsync(AuditArchiveBatch batch, HeldRecords held, CancellationToken cancellationToken)
    {
        using var stored = await storage.GetAsync(batch.BlobKey, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return false;
        }

        using var buffer = new MemoryStream();
        await stored.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return AuditArchiveFormat.Read(buffer.ToArray()).Any(e =>
            e.Content.RecordType is { } type && e.Content.RecordId is { } id && (held.Records.Contains((type, id)) || held.Masters.Contains((type, id))));
    }
}

/// <summary>What a search of the archive asks: its date range, which it must have, and the trail's own filters.</summary>
public sealed record ArchiveQuery(DateTimeOffset From, DateTimeOffset To, string? EventType, Guid? ActorId, Guid? ImpersonatorId, string? RecordType, Guid? RecordId, string? Source);

/// <summary>
/// Searches the archived part of the trail (TF-064 AC2, ADR-0050): the archive index gives the batches whose date range
/// meets the search's, each batch is read from the container, and its events are filtered as the database's are, oldest
/// first. A pruned batch is gone; the index row stays, and the search passes it by.
/// </summary>
public sealed class AuditArchiveSearch(AuditDbContext context, IObjectStorage storage)
{
    public const int MaxRows = 500;

    public async Task<(IReadOnlyList<SealedEvent> Events, int Batches, bool Truncated)> SearchAsync(ArchiveQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var prunedUpTo = await context.Prunes.AsNoTracking().MaxAsync(p => (long?)p.UpToPosition, cancellationToken).ConfigureAwait(false) ?? 0;
        var batches = await context.ArchiveBatches.AsNoTracking()
            .Where(b => b.LastPosition > prunedUpTo && b.FirstAt <= query.To && b.LastAt >= query.From)
            .OrderBy(b => b.FirstPosition)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var found = new List<SealedEvent>();
        foreach (var batch in batches)
        {
            using var stored = await storage.GetAsync(batch.BlobKey, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                continue;
            }

            using var buffer = new MemoryStream();
            await stored.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            found.AddRange(AuditArchiveFormat.Read(buffer.ToArray()).Where(e => Matches(e.Content, query)));
        }

        var ordered = found.OrderBy(e => e.Content.CreatedAt).ThenBy(e => e.Position).ToList();
        return (ordered.Take(MaxRows).ToList(), batches.Count, ordered.Count > MaxRows);
    }

    private static bool Matches(ChainedEvent e, ArchiveQuery q) =>
        e.CreatedAt >= q.From && e.CreatedAt <= q.To
        && (q.EventType is null || e.EventType == q.EventType)
        && (q.ActorId is null || e.ActorId == q.ActorId)
        && (q.ImpersonatorId is null || e.ImpersonatorId == q.ImpersonatorId)
        && (q.RecordType is null || e.RecordType == q.RecordType)
        && (q.RecordId is null || e.RecordId == q.RecordId)
        && (q.Source is null || e.Source == q.Source);
}

/// <summary>
/// Writes an audit search to CSV (TF-064, TF-060): the search's filters and the time in the first lines, then one row per
/// event in the order searched, kept as a file in the tenant's container and handed over as a download. The export is
/// itself an audit event. A text that starts like a spreadsheet formula is written with a leading apostrophe.
/// </summary>
public sealed class AuditExporter(AuditDbContext context, IObjectStorage storage, IUnitOfWork unitOfWork, IAuditWriter audit, ICurrentPrincipal actor, TimeProvider time, AuditEventNames? eventNames = null)
{
    public const int MaxRows = 100_000;

    public static readonly string[] Columns =
    [
        "id", "created_at", "event_type", "event_name", "outcome", "actor_id", "actor_name", "on_behalf_of_id", "impersonator_id", "impersonator_name",
        "source", "severity", "target", "record_type", "record_id", "reason", "correlation_id", "details", "chain_position", "hash",
    ];

    public async Task<AuditExport> ExportAsync(IQueryable<AuditEventRecord> search, IReadOnlyList<string> filters, IReadOnlyDictionary<Guid, string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(names);
        var rows = await search.Take(MaxRows).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var csv = new StringBuilder();
        csv.Append("# Audit trail export, ").Append(AuditCanonicalForm.Time(now)).Append(" UTC, ").Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append(" events\n");
        csv.Append("# Filters: ").Append(filters.Count == 0 ? "none" : string.Join("; ", filters)).Append('\n');
        csv.Append(string.Join(',', Columns)).Append('\n');
        foreach (var e in rows)
        {
            string?[] values =
            [
                e.Id.ToString("D"), AuditCanonicalForm.Time(e.CreatedAt), e.EventType, eventNames?.NameOf(e.EventType) ?? e.EventType, e.Outcome, e.ActorId.ToString("D"), names.GetValueOrDefault(e.ActorId),
                e.OnBehalfOfId?.ToString("D"), e.ImpersonatorId?.ToString("D"), e.ImpersonatorId is { } impersonator ? names.GetValueOrDefault(impersonator) : null,
                e.Source, e.Severity, e.Target, e.RecordType, e.RecordId?.ToString("D"), e.Reason, e.CorrelationId, e.Details,
                e.ChainPosition?.ToString(CultureInfo.InvariantCulture), e.Hash,
            ];
            csv.Append(string.Join(',', values.Select(Field))).Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        var id = Ids.New();
        var key = $"audit/exports/{id:D}.csv";
        await using (var stream = new MemoryStream(bytes))
        {
            await storage.PutAsync(key, stream, "text/csv; charset=utf-8", cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var export = new AuditExport(System.Text.Json.JsonSerializer.Serialize(filters), key, rows.Count, AuditArchiveFormat.Sha256(bytes));
        context.Exports.Add(export);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["export_id"] = export.Id.ToString("D"),
            ["rows"] = rows.Count.ToString(CultureInfo.InvariantCulture),
            ["filters"] = filters.Count == 0 ? "none" : string.Join("; ", filters),
        };
        await audit.WriteAsync(new AuditEvent(AuditEvents.EventsExported, AuditOutcome.Success, actor.Id, "audit trail", details, "audit_export", export.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return export;
    }

    /// <summary>A CSV field: quoted when it holds a comma, a quote or a line break; a formula's first character defused.</summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return text.AsSpan().IndexOfAny(",\"\n\r") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }
}

/// <summary>
/// <c>audit.retention</c> (TF-064, TF-066, TF-124, ADR-0050, ADR-0061): the unit's daily pass of the trail's and the
/// records' life: the archive after the online period, the oldest end's pruning after the trail's retention, and the
/// records past their classes' retention, sessions among them, which is the sessions' cleanup. Each step is idempotent:
/// a run repeated archives, prunes and deletes nothing it did.
/// </summary>
public sealed class AuditRetentionJob(AuditArchiver archiver, AuditPruner pruner, RecordRetentionRunner records, ILogger<AuditRetentionJob> logger) : IJob
{
    public static readonly JobDefinition Definition = JobDefinition.Create<AuditRetentionJob>(
        "audit.retention", "Archives the audit trail past its online period, prunes it past its retention, and deletes the records past their classes' retention, sessions among them.", "Platform team",
        JobSchedule.Interval(TimeSpan.FromDays(1), firstAfter: TimeSpan.FromMinutes(10)), PriorityClass.Bulk, TimeSpan.FromHours(2));

    public async Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var archived = await archiver.ArchiveAsync(cancellationToken).ConfigureAwait(false);
        var pruned = await pruner.PruneAsync(cancellationToken).ConfigureAwait(false);
        var runs = await records.RunAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Retention: {Archived} audit events archived, the trail pruned up to {Pruned}, {Deleted} records deleted and {Held} held.",
            archived,
            pruned?.UpToPosition,
            runs.Sum(r => r.Deleted),
            runs.Sum(r => r.Held));
        context.Log(string.Create(CultureInfo.InvariantCulture, $"{archived} audit event(s) archived; the trail pruned up to {pruned?.UpToPosition.ToString(CultureInfo.InvariantCulture) ?? "nothing"}; {runs.Sum(r => r.Deleted)} record(s) deleted and {runs.Sum(r => r.Held)} held."));
        return Result.Success();
    }
}
