using Iron.Kernel.Data.Transactions;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Data.Retention;

/// <summary>What the legal holds keep now (TF-066 AC2): the whole tenant, single records, and every record of a master.</summary>
public sealed record HeldRecords(bool Tenant, IReadOnlySet<(string Type, Guid Id)> Records, IReadOnlySet<(string Type, Guid Id)> Masters)
{
    public static readonly HeldRecords None = new(false, new HashSet<(string, Guid)>(), new HashSet<(string, Guid)>());

    /// <summary>True when a hold names this record, its master, or the whole tenant.</summary>
    public bool Holds(string recordType, RetainedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Tenant
            || Records.Contains((recordType, record.Id))
            || (record.MasterType is { } masterType && record.MasterId is { } masterId && Masters.Contains((masterType, masterId)));
    }
}

/// <summary>The legal holds standing now (ADR-0050): the Audit module keeps them; without it, nothing is held.</summary>
public interface ILegalHolds
{
    Task<HeldRecords> ActiveAsync(CancellationToken cancellationToken);
}

/// <summary>Where a record goes before it is deleted (TF-066 AC1): the tenant's container, in the Audit module. Answers the copy's key.</summary>
public interface IRecordArchive
{
    Task<string> ArchiveAsync(string recordType, RetentionClass retention, RetainedRecord record, CancellationToken cancellationToken);
}

/// <summary>How many days a class keeps its records in this tenant: its choice within the operator's limits.</summary>
public interface IRetentionPeriods
{
    Task<int> DaysAsync(RetentionClass retention, CancellationToken cancellationToken);
}

/// <summary>A record deleted after its retention, as the audit trail records it (TF-066 AC1).</summary>
public sealed record RecordExpired(string RecordType, Guid RecordId, string RetentionClass, DateTimeOffset RetainedFrom, int Days, string ArchiveKey);

/// <summary>The audit event of each record the retention job deleted, written in the deletion's transaction (ADR-0013).</summary>
public interface IRetentionAudit
{
    Task ExpiredAsync(RecordExpired expired, CancellationToken cancellationToken);
}

/// <summary>What one run of the retention job did for one record type.</summary>
public sealed record RetentionRun(string RecordType, string RetentionClass, int Days, int Deleted, int Held);

/// <summary>
/// The retention job (TF-066, ADR-0050): for each record type a module declared, the records whose retention began before
/// the class's period are archived, one by one, then deleted together with an audit event each, in one transaction;
/// a record a legal hold names, by itself, through its master, or through a hold on the whole tenant, is skipped and
/// stays. A failure leaves the records in place; their archive copies are written again at the next run.
/// </summary>
public sealed class RecordRetentionRunner(
    IEnumerable<IRetainedRecords> types,
    ILegalHolds holds,
    IRecordArchive archive,
    IRetentionPeriods periods,
    IRetentionAudit audit,
    IUnitOfWork unitOfWork,
    TimeProvider time,
    ILogger<RecordRetentionRunner> logger)
{
    /// <summary>At most this many records of one type in one run, so a run stays a short transaction.</summary>
    public const int BatchSize = 200;

    public async Task<IReadOnlyList<RetentionRun>> RunAsync(CancellationToken cancellationToken)
    {
        var held = await holds.ActiveAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var runs = new List<RetentionRun>();
        foreach (var type in types)
        {
            var days = await periods.DaysAsync(type.Class, cancellationToken).ConfigureAwait(false);
            var expired = await type.ExpiredAsync(now.AddDays(-days), BatchSize, cancellationToken).ConfigureAwait(false);
            var kept = expired.Where(record => held.Holds(type.RecordType, record)).ToList();
            var going = expired.Where(record => !held.Holds(type.RecordType, record)).ToList();
            if (going.Count > 0)
            {
                var keys = new List<string>(going.Count);
                foreach (var record in going)
                {
                    keys.Add(await archive.ArchiveAsync(type.RecordType, type.Class, record, cancellationToken).ConfigureAwait(false));
                }

                await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
                await type.DeleteAsync([.. going.Select(record => record.Id)], cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < going.Count; i++)
                {
                    await audit.ExpiredAsync(new RecordExpired(type.RecordType, going[i].Id, type.Class.Code, going[i].RetainedFrom, days, keys[i]), cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (kept.Count > 0)
            {
                logger.LogInformation("Retention kept {Held} {RecordType} record(s) past their {Days} days under a legal hold.", kept.Count, type.RecordType, days);
            }

            runs.Add(new RetentionRun(type.RecordType, type.Class.Code, days, going.Count, kept.Count));
        }

        return runs;
    }
}
