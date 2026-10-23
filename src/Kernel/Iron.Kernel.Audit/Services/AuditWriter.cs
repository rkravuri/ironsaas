using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Telemetry.Correlation;
using Microsoft.AspNetCore.Http;

namespace Iron.Kernel.Audit.Services;

/// <summary>
/// Writes one row per event on the scope's connection. The correlation id comes from the request, or from the current
/// activity in a job; the source is <c>api</c> inside a request and <c>job</c> outside one (the shell marks <c>ui</c> in T15).
/// A request made by someone signed in as another person names the impersonator on every event it writes, by id and, in
/// the details, by name, without the module saying so (TF-025 AC2, ADR-0049).
/// </summary>
public sealed class AuditWriter(AuditDbContext context, IHttpContextAccessor accessor, ICurrentPrincipal current) : IAuditWriter
{
    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        var http = accessor.HttpContext;
        var correlation = http is null ? CorrelationId.Current() : RequestCorrelation.Get(http);
        var source = http is null ? AuditSource.Job : AuditSource.Api;
        var impersonatorId = http is null ? null : current.ImpersonatorId;
        var values = auditEvent.Details;
        if (impersonatorId is not null && current.ImpersonatorName is { } impersonator && values?.ContainsKey(ImpersonatorKey) != true)
        {
            values = new Dictionary<string, string>(values ?? new Dictionary<string, string>(), StringComparer.Ordinal) { [ImpersonatorKey] = impersonator };
        }

        var details = values is null ? null : JsonSerializer.Serialize(values);

        // The fields a save changed (TF-062), as the canonical form hashes them: field, old, new, treatment.
        var changes = auditEvent.Changes is not { Count: > 0 } fields
            ? null
            : JsonSerializer.Serialize(fields.Select(f => new Dictionary<string, string?>(StringComparer.Ordinal) { ["field"] = f.Field, ["old"] = f.Old, ["new"] = f.New, ["treatment"] = f.Treatment }));

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
            auditEvent.OnBehalfOfId ?? (http is null ? null : current.ActingForId),
            impersonatorId,
            auditEvent.Severity,
            changes));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The detail that names the impersonator in words, which the list answers when no principal here has their id.</summary>
    public const string ImpersonatorKey = "impersonator";
}
