using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.History;
using Iron.Kernel.History;

namespace Iron.Kernel.Audit.Services;

/// <summary>
/// Writes a save's field-level changes into the audit trail (TF-062, ADR-0051): one event per created, changed or deleted
/// tracked record, named <c>record.created</c>, <c>record.changed</c> or <c>record.deleted</c>, with the record's type,
/// id and version, every changed field, and the reason the scope was given. The writer adds the actor's impersonator or
/// the person acted for, the source and the correlation id, and the sealer chains it like every other event.
/// </summary>
public sealed class AuditChangeRecorder(IAuditWriter audit, ICurrentPrincipal actor, IChangeReason reason) : IChangeRecorder
{
    public async Task RecordAsync(IReadOnlyList<RecordChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var change in changes)
        {
            var details = change.Version is { } version
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["version"] = version.ToString(CultureInfo.InvariantCulture) }
                : null;
            var eventType = change.Kind switch
            {
                RecordChangeKinds.Created => AuditEvents.RecordCreated,
                RecordChangeKinds.Deleted => AuditEvents.RecordDeleted,
                _ => AuditEvents.RecordChanged,
            };
            await audit.WriteAsync(
                new AuditEvent(eventType, AuditOutcome.Success, actor.Id, null, details, change.RecordType, change.RecordId, reason.Reason, Changes: change.Fields),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
