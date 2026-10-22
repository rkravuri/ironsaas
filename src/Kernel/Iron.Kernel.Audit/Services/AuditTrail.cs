using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Audit.Services;

/// <summary>Reads one principal's actions from the trail, oldest first (TF-026 AC3): what they did, and what was done as them.</summary>
public sealed class AuditTrail(AuditDbContext context) : IAuditTrail
{
    public async Task<IReadOnlyList<AuditTrailEntry>> ActionsOfAsync(Guid principalId, DateTimeOffset from, CancellationToken cancellationToken) =>
        await context.Events.AsNoTracking()
            .Where(e => (e.ActorId == principalId || e.ImpersonatorId == principalId) && e.CreatedAt >= from)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .Select(e => new AuditTrailEntry(e.Id, e.CreatedAt, e.EventType, e.Outcome, e.Target, e.RecordType, e.RecordId, e.Reason))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
