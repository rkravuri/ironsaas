using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Audit.Services;

/// <summary>A restore an operator accepted, as the control plane tells the unit (ADR-0050).</summary>
public sealed record AcceptedRestoreNotice(
    int Segment,
    long StartPosition,
    string PreviousHash,
    long RestorePosition,
    DateTimeOffset RestoreAt,
    long LostFrom,
    long LostTo,
    string Ticket,
    string Reason,
    string AcceptedBy,
    DateTimeOffset AcceptedAt);

/// <summary>
/// Records in the tenant's own trail each restore an operator accepted in the control plane (ADR-0050): a row the chain
/// page shows for good, with the restore point and the range lost, and the event <c>audit.restore_accepted</c>, in one
/// transaction. A restore recorded before is left as it is, so the unit may hear of it any number of times.
/// </summary>
public sealed class AuditRestoreRecorder(AuditDbContext context, IAuditWriter audit, IUnitOfWork unitOfWork, ILogger<AuditRestoreRecorder> logger)
{
    public async Task<int> RecordAsync(IReadOnlyList<AcceptedRestoreNotice> restores, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(restores);
        if (restores.Count == 0)
        {
            return 0;
        }

        var known = await context.ChainRestores.AsNoTracking().Select(r => r.Segment).ToListAsync(cancellationToken).ConfigureAwait(false);
        var recorded = 0;
        foreach (var restore in restores.Where(r => !known.Contains(r.Segment)).OrderBy(r => r.Segment))
        {
            await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
            var row = new AuditChainRestore(restore.Segment, restore.StartPosition, restore.PreviousHash, restore.RestorePosition, restore.RestoreAt, restore.LostFrom, restore.LostTo, restore.Ticket, restore.Reason, restore.AcceptedBy, restore.AcceptedAt);
            context.ChainRestores.Add(row);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["segment"] = restore.Segment.ToString(CultureInfo.InvariantCulture),
                ["restore_position"] = restore.RestorePosition.ToString(CultureInfo.InvariantCulture),
                ["lost_from"] = restore.LostFrom.ToString(CultureInfo.InvariantCulture),
                ["lost_to"] = restore.LostTo.ToString(CultureInfo.InvariantCulture),
                ["ticket"] = restore.Ticket,
                ["accepted_by"] = restore.AcceptedBy,
            };
            await audit.WriteAsync(new AuditEvent(AuditEvents.RestoreAccepted, AuditOutcome.Success, SystemPrincipal.SystemId, "audit trail", details, AuditChainRestore.RecordType, row.Id, restore.Reason, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            logger.LogWarning("The operator {Operator} accepted the restore of this tenant's audit chain to position {Position}, ticket {Ticket}: segment {Segment} continues from there.", restore.AcceptedBy, restore.RestorePosition, restore.Ticket, restore.Segment);
            recorded++;
        }

        return recorded;
    }
}
