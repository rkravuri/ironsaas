using Iron.Kernel.Samples.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Samples.Data;

/// <summary>
/// The sample module's own subscriber (ADR-0060): it marks a new sample record noticed, so the smoke sees an event go from
/// a request through the outbox and the dispatcher to a handler's change. A repeated delivery finds the inbox handled and
/// does not run; were it to run, the record keeps its first time.
/// </summary>
public sealed class NoticeCreatedSample(SamplesDbContext context, TimeProvider time) : IEventHandler<SampleRecordCreated>
{
    public const string Subscription = "samples.notice_created";

    public async Task<Result> HandleAsync(DomainEvent<SampleRecordCreated> domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var record = await context.Records.SingleOrDefaultAsync(r => r.Id == domainEvent.Data.RecordId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Result.Success();
        }

        record.Notice(time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
