using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Policies;

namespace Iron.Kernel.Audit.Services;

/// <summary>
/// The data library's record events, written to the audit trail (TF-222 AC2, ADR-0038): a cross-unit read by id with
/// what allowed it, a refused read as the reader was answered, one event per list page with the ids of other units'
/// records and their count, and a sharing change with the units before and after.
/// </summary>
public sealed class RecordAuditWriter(IAuditWriter audit) : IRecordAudit
{
    public Task ReadAsync(RecordReadEvent read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["owning_unit_id"] = read.OwningUnitId.ToString("D"),
            ["action"] = read.Action,
            ["allowed_by"] = read.AllowedBy,
        };
        return audit.WriteAsync(new AuditEvent(RecordAuditEvents.Read, AuditOutcome.Success, read.ReaderId, read.RecordType, details, read.RecordType, read.RecordId), cancellationToken);
    }

    public Task RefusedAsync(RecordRefusedEvent refused, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refused);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["owning_unit_id"] = refused.OwningUnitId.ToString("D"),
            ["action"] = refused.Action,
            ["answer"] = refused.Answer,
        };
        return audit.WriteAsync(new AuditEvent(RecordAuditEvents.Read, AuditOutcome.Denied, refused.ReaderId, refused.RecordType, details, refused.RecordType, refused.RecordId, refused.Answer), cancellationToken);
    }

    public Task ListedAsync(RecordsListedEvent listed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listed);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["count"] = listed.RecordIds.Count.ToString(CultureInfo.InvariantCulture),
            ["record_ids"] = string.Join(',', listed.RecordIds.Select(id => id.ToString("D"))),
        };
        return audit.WriteAsync(new AuditEvent(RecordAuditEvents.Listed, AuditOutcome.Success, listed.ReaderId, listed.RecordType, details, listed.RecordType), cancellationToken);
    }

    public Task SharingChangedAsync(RecordSharingEvent change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        static string Words(IReadOnlyList<Guid> units) => string.Join(',', units.Select(u => u.ToString("D")));
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["owning_unit_id"] = change.OwningUnitId.ToString("D"),
            ["mode_before"] = change.ModeBefore.ToString().ToLowerInvariant(),
            ["units_before"] = Words(change.UnitsBefore),
            ["mode_after"] = change.ModeAfter.ToString().ToLowerInvariant(),
            ["units_after"] = Words(change.UnitsAfter),
        };
        return audit.WriteAsync(new AuditEvent(RecordAuditEvents.SharingChanged, AuditOutcome.Success, change.ActorId, change.RecordType, details, change.RecordType, change.RecordId), cancellationToken);
    }
}
