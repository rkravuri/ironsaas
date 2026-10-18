using System.Text.Json;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Data.Lists;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Audit.Api;

public sealed record AuditEventResponse(
    Guid Id,
    string EventType,
    string Outcome,
    Guid ActorId,
    string Source,
    string? Target,
    string? RecordType,
    Guid? RecordId,
    string? Reason,
    string? CorrelationId,
    IReadOnlyDictionary<string, string>? Details,
    DateTimeOffset CreatedAt,
    Guid CreatedBy)
{
    public static AuditEventResponse From(AuditEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var details = record.Details is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(record.Details);
        return new AuditEventResponse(
            record.Id,
            record.EventType,
            record.Outcome,
            record.ActorId,
            record.Source,
            record.Target,
            record.RecordType,
            record.RecordId,
            record.Reason,
            record.CorrelationId,
            details,
            record.CreatedAt,
            record.CreatedBy);
    }
}

internal static class AuditEventsEndpoints
{
    /// <summary>The whitelist of the list grammar (TF-064: search by time, actor, record and type).</summary>
    public static readonly ListFields<AuditEventRecord> Fields = new ListFields<AuditEventRecord>()
        .Add("id", e => e.Id, FieldUse.Select | FieldUse.Filter)
        .Add("event_type", e => e.EventType)
        .Add("outcome", e => e.Outcome, FieldUse.Select | FieldUse.Filter)
        .Add("actor_id", e => e.ActorId, FieldUse.Select | FieldUse.Filter)
        .Add("source", e => e.Source, FieldUse.Select | FieldUse.Filter)
        .Add("target", e => e.Target, FieldUse.Select)
        .Add("record_type", e => e.RecordType, FieldUse.Select | FieldUse.Filter)
        .Add("record_id", e => e.RecordId, FieldUse.Select | FieldUse.Filter)
        .Add("reason", e => e.Reason, FieldUse.Select)
        .Add("correlation_id", e => e.CorrelationId, FieldUse.Select | FieldUse.Filter)
        .Add("details", e => e.Details, FieldUse.Select)
        .Add("created_at", e => e.CreatedAt, FieldUse.Select | FieldUse.Sort | FieldUse.Filter)
        .Add("created_by", e => e.CreatedBy, FieldUse.Select | FieldUse.Filter)
        .SortByDefault("created_at", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var events = endpoints.MapApiGroup("audit-events").RequirePermission(AuditPermissions.EventsRead);
        events.MapGet("/", ListAsync).WithName("ListAuditEvents").WithSummary("Search the audit trail of this tenant.");
    }

    private static async Task<IResult> ListAsync(HttpContext http, AuditDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.Events.AsNoTracking().ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(AuditEventResponse.From), http, query);
    }
}
