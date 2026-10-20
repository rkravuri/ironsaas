using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Telemetry.Correlation;
using Microsoft.AspNetCore.Http;

namespace Iron.Kernel.Audit.Services;

/// <summary>
/// Writes one row per event on the scope's connection. The correlation id comes from the request, or from the current
/// activity in a job; the source is <c>api</c> inside a request and <c>job</c> outside one (the shell marks <c>ui</c> in T15).
/// </summary>
public sealed class AuditWriter(AuditDbContext context, IHttpContextAccessor accessor, ICurrentPrincipal current) : IAuditWriter
{
    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        var http = accessor.HttpContext;
        var correlation = http is null ? CorrelationId.Current() : RequestCorrelation.Get(http);
        var source = http is null ? AuditSource.Job : AuditSource.Api;
        var details = auditEvent.Details is null ? null : JsonSerializer.Serialize(auditEvent.Details);

        context.Events.Add(new AuditEventRecord(
            auditEvent.Action,
            auditEvent.Outcome,
            auditEvent.PrincipalId,
            source,
            auditEvent.Target,
            auditEvent.RecordType,
            auditEvent.RecordId,
            auditEvent.Reason,
            details,
            correlation.IsEmpty ? null : correlation.Value,
            // A request made while acting for someone names them on every event it writes (ADR-0037); a job never acts.
            auditEvent.OnBehalfOfId ?? (http is null ? null : current.ActingForId)));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
