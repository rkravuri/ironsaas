using System.Text.Json;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Lists;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
    Guid CreatedBy,
    Guid? OnBehalfOfId = null,
    string? ActorName = null,
    string? OnBehalfOfName = null,
    Guid? ImpersonatorId = null,
    string? ImpersonatorName = null,
    string Severity = AuditSeverity.Normal)
{
    public static AuditEventResponse From(AuditEventRecord record, IReadOnlyDictionary<Guid, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var response = From(record);
        return response with
        {
            ActorName = names.GetValueOrDefault(record.ActorId),
            OnBehalfOfName = record.OnBehalfOfId is { } onBehalfOf ? names.GetValueOrDefault(onBehalfOf) : null,

            // The operator is nobody in a unit: the name the session kept, in the details, stands for them (ADR-0049).
            ImpersonatorName = record.ImpersonatorId is { } impersonator
                ? names.GetValueOrDefault(impersonator) ?? response.Details?.GetValueOrDefault(AuditWriter.ImpersonatorKey)
                : null,
        };
    }

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
            record.CreatedBy,
            record.OnBehalfOfId,
            ImpersonatorId: record.ImpersonatorId,
            Severity: record.Severity);
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
        .Add("on_behalf_of_id", e => e.OnBehalfOfId, FieldUse.Select | FieldUse.Filter)
        .Add("impersonator_id", e => e.ImpersonatorId, FieldUse.Select | FieldUse.Filter)
        .Add("severity", e => e.Severity, FieldUse.Select | FieldUse.Filter)
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
        events.MapGet("/", ListAsync).Produces<ListResponse<AuditEventResponse>>(StatusCodes.Status200OK).WithName("ListAuditEvents").WithSummary("Search the audit trail of this tenant.");
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

        // Every person by name, the actor, the one acted for (TF-037 AC3) and the impersonator (TF-025 AC2), so a reader
        // does not look the ids up. A host without Identity, such as the audit tests' own, answers the ids alone.
        IReadOnlyDictionary<Guid, string> names = new Dictionary<Guid, string>();
        if (http.RequestServices.GetService<IPrincipals>() is { } principals)
        {
            var ids = page.Items.Select(e => e.ActorId)
                .Concat(page.Items.Where(e => e.OnBehalfOfId is not null).Select(e => e.OnBehalfOfId!.Value))
                .Concat(page.Items.Where(e => e.ImpersonatorId is not null).Select(e => e.ImpersonatorId!.Value))
                .Distinct().ToList();
            names = (await principals.FindAsync(ids, cancellationToken)).ToDictionary(p => p.Key, p => p.Value.DisplayName);
        }

        return ApiResults.List(page.Map(record => AuditEventResponse.From(record, names)), http, query);
    }
}
