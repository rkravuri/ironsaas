using System.Text.Json;
using Iron.Audit.Contracts;
using Iron.Audit.Data;
using Iron.Core.Correlation;
using Iron.Telemetry.Correlation;
using Microsoft.AspNetCore.Http;

namespace Iron.Audit.Services;

/// <summary>
/// Writes one row per event on the scope's connection. The correlation id comes from the request, or from the current
/// activity in a job; the source is <c>api</c> inside a request and <c>job</c> outside one (the shell marks <c>ui</c> in T15).
/// </summary>
public sealed class AuditWriter(AuditDbContext context, IHttpContextAccessor accessor) : IAuditWriter
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
            correlation.IsEmpty ? null : correlation.Value));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
