using Iron.Audit.Contracts;
using Microsoft.Extensions.Logging;

namespace Iron.Audit.Services;

/// <summary>Writes audit events as structured log lines. Replaced by the database writer in T9.</summary>
public sealed class LoggingAuditWriter(ILogger<LoggingAuditWriter> logger) : IAuditWriter
{
    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var details = auditEvent.Details is null ? string.Empty : string.Join(", ", auditEvent.Details.Select(d => $"{d.Key}={d.Value}"));
        logger.LogInformation(
            "Audit {Action} {Outcome} by {PrincipalId} on {Target} ({Details})",
            auditEvent.Action,
            auditEvent.Outcome,
            auditEvent.PrincipalId,
            auditEvent.Target ?? "-",
            details);
        return Task.CompletedTask;
    }
}
