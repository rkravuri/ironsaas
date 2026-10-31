using System.Globalization;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Events.Data;
using Iron.Kernel.Files.Contracts;
using Iron.Kernel.Settings.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Events.Services;

/// <summary>
/// The retention of the outbox (TF-112 AC2, ADR-0060): an event older than the tenant's retention that every subscriber
/// handled is written, with its deliveries, to the tenant's container as one line of JSON, then leaves the outbox and the
/// inbox, a thousand at a time, each batch recorded and audited. A dead letter or a pending delivery keeps its event: it is
/// work someone still owes. A search for an archived event says it was archived.
/// </summary>
public sealed class EventArchiver(EventsDbContext context, IUnitOfWork unitOfWork, IAuditWriter audit, ISettings settings, IObjectStorage storage, TimeProvider time, ILogger<EventArchiver> logger)
{
    public const int BatchSize = 1000;

    /// <summary>Where the archives are, in the tenant's container.</summary>
    public const string Prefix = "events/archive/";

    private static readonly JsonSerializerOptions Lines = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var days = await settings.GetAsync(EventsSettings.RetentionDays, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var cutoff = time.GetUtcNow().AddDays(-days);
        var total = 0;
        while (true)
        {
            var batch = await context.Outbox.AsNoTracking()
                .Where(e => e.OccurredAt < cutoff && e.FannedAt != null && !context.Inbox.Any(i => i.EventId == e.Id && i.Status != DeliveryStates.Handled))
                .OrderBy(e => e.Position)
                .Take(BatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            var ids = batch.Select(e => e.Id).ToList();
            var deliveries = await context.Inbox.AsNoTracking().Where(i => ids.Contains(i.EventId)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var byEvent = deliveries.ToLookup(d => d.EventId);
            var key = string.Create(CultureInfo.InvariantCulture, $"{Prefix}{batch[0].Position:D12}-{batch[^1].Position:D12}.jsonl");
            var text = new StringBuilder();
            foreach (var outboxEvent in batch)
            {
                using var data = JsonDocument.Parse(outboxEvent.Data);
                var line = new
                {
                    outboxEvent.Id,
                    outboxEvent.Position,
                    outboxEvent.Type,
                    Version = outboxEvent.EventVersion,
                    outboxEvent.RecordType,
                    outboxEvent.RecordId,
                    outboxEvent.Tenant,
                    outboxEvent.ActorId,
                    outboxEvent.OnBehalfOfId,
                    outboxEvent.OccurredAt,
                    outboxEvent.CorrelationId,
                    Data = data.RootElement,
                    Deliveries = byEvent[outboxEvent.Id].Select(d => new { d.Subscriber, d.Status, d.Attempts, d.HandledAt, d.Replays }),
                };
                text.Append(JsonSerializer.Serialize(line, Lines)).Append('\n');
            }

            // Written before the rows go: a crash between the two writes the same object again on the next pass.
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text.ToString())))
            {
                await storage.PutAsync(key, stream, "application/x-ndjson", cancellationToken).ConfigureAwait(false);
            }

            await using (var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
            {
                await context.Inbox.Where(i => ids.Contains(i.EventId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await context.Outbox.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                context.Archives.Add(new EventArchive(batch[0].Position, batch[^1].Position, batch[0].OccurredAt, batch.Max(e => e.OccurredAt), batch.Count, key));
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                var details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["events"] = batch.Count.ToString(CultureInfo.InvariantCulture),
                    ["from_position"] = batch[0].Position.ToString(CultureInfo.InvariantCulture),
                    ["to_position"] = batch[^1].Position.ToString(CultureInfo.InvariantCulture),
                    ["object"] = key,
                    ["retention_days"] = days.ToString(CultureInfo.InvariantCulture),
                };
                await audit.WriteAsync(new AuditEvent(EventsAuditEvents.EventsArchived, AuditOutcome.Success, SystemPrincipal.SystemId, key, details), cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            total += batch.Count;
            logger.LogInformation("Archived {Count} events past the retention of {Days} days to {Key}.", batch.Count, days, key);
            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        return total;
    }
}

/// <summary>
/// <c>events.archive</c> (TF-112 AC2, TF-124, ADR-0060, ADR-0061): every hour, the events past the tenant's retention that
/// every subscriber handled go to its container. Idempotent: a repeated run finds them gone and archives nothing.
/// </summary>
public sealed class EventArchiveJob(EventArchiver archiver) : IJob
{
    public static readonly JobDefinition Definition = JobDefinition.Create<EventArchiveJob>(
        "events.archive", "Archives the domain events every subscriber handled, past the tenant's retention, to its container.", "Platform team",
        JobSchedule.Interval(TimeSpan.FromHours(1), firstAfter: TimeSpan.FromMinutes(1)), PriorityClass.Bulk, TimeSpan.FromMinutes(30));

    public async Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var archived = await archiver.RunAsync(cancellationToken).ConfigureAwait(false);
        context.Log($"{archived} event(s) archived.");
        return Result.Success();
    }
}
