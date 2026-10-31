using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Events.Data;
using Iron.Kernel.Telemetry.Correlation;
using Microsoft.AspNetCore.Http;

namespace Iron.Kernel.Events.Services;

/// <summary>
/// The outbox's writer (TF-085, ADR-0060). It writes only inside the unit of work of the change, so the event commits with
/// the change and its audit event, or rolls back with them: no event exists without its change, and no committed change
/// loses its event. The actor, the person acted for and the correlation id come from the request, as the audit writer's do,
/// or from the job; the time is the server's.
/// </summary>
public sealed class EventPublisher(
    EventsDbContext context,
    IUnitOfWork unitOfWork,
    EventDeclarations declarations,
    ICurrentPrincipal current,
    IHttpContextAccessor accessor,
    TimeProvider time,
    TenantContext? tenant = null) : IEventPublisher
{
    public async Task<Guid> PublishAsync<TData>(EventType<TData> type, Guid recordId, TData data, CancellationToken cancellationToken)
        where TData : class
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(data);
        if (recordId == Guid.Empty)
        {
            throw new ArgumentException($"{type} names the record it is about; the id is empty.", nameof(recordId));
        }

        if (!unitOfWork.IsActive)
        {
            throw new InvalidOperationException($"{type} is published inside the unit of work of the change that raises it (ADR-0060): begin one, make the change, publish, then commit.");
        }

        if (declarations.Find(type.Name, type.Version) is null)
        {
            throw new InvalidOperationException($"{type} is not declared by a module of this host: list it in the publishing module's Events.");
        }

        // Throws for data that does not fit the declaration (TF-110 AC2): nothing is written.
        var json = type.Serialize(data);
        var http = accessor.HttpContext;
        var correlation = http is null ? CorrelationId.Current() : RequestCorrelation.Get(http);
        var outboxEvent = new OutboxEvent(
            type.Name,
            type.Version,
            type.RecordType,
            recordId,
            // A unit is bound to its tenant; a host without the binding (a module's test) writes none.
            tenant?.Id.Value ?? string.Empty,
            current.Id,
            http is null ? null : current.ActingForId,
            time.GetUtcNow(),
            correlation.IsEmpty ? null : correlation.Value,
            json.GetRawText());
        context.Outbox.Add(outboxEvent);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return outboxEvent.Id;
    }
}
