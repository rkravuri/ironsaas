using System.Text.Json;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Modules;
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
    /// <summary>The event's place in the chain (TF-061); null until the sealer gave it one.</summary>
    public long? ChainPosition { get; init; }

    /// <summary>The words the event type reads as (ADR-0051), or its code when no module of this host declares it.</summary>
    public string? EventName { get; init; }

    /// <summary>True for an event read from the archive rather than the database (TF-064 AC2).</summary>
    public bool Archived { get; init; }

    /// <summary>The fields a save changed, for a record's change event (TF-062): one group of rows per save.</summary>
    public IReadOnlyList<FieldChangeResponse>? Changes { get; init; }

    /// <summary>A change event's fields from the column's JSON.</summary>
    public static IReadOnlyList<FieldChangeResponse>? ChangesOf(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<FieldChangeResponse>>(json, ChangesJson);

    private static readonly JsonSerializerOptions ChangesJson = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

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

    /// <summary>An archived event as the list answers a database row (TF-064 AC2), named the same way.</summary>
    public static AuditEventResponse FromArchive(SealedEvent archived, IReadOnlyDictionary<Guid, string> names)
    {
        ArgumentNullException.ThrowIfNull(archived);
        ArgumentNullException.ThrowIfNull(names);
        var e = archived.Content;
        var details = e.Details is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(e.Details);
        return new AuditEventResponse(
            e.Id, e.EventType, e.Outcome, e.ActorId, e.Source, e.Target, e.RecordType, e.RecordId, e.Reason, e.CorrelationId, details, e.CreatedAt, e.CreatedBy, e.OnBehalfOfId,
            names.GetValueOrDefault(e.ActorId),
            e.OnBehalfOfId is { } onBehalfOf ? names.GetValueOrDefault(onBehalfOf) : null,
            e.ImpersonatorId,
            e.ImpersonatorId is { } impersonator ? names.GetValueOrDefault(impersonator) ?? details?.GetValueOrDefault(AuditWriter.ImpersonatorKey) : null,
            e.Severity)
        {
            ChainPosition = archived.Position,
            Archived = true,
            Changes = ChangesOf(e.Changes),
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
            Severity: record.Severity)
        {
            ChainPosition = record.ChainPosition,
            Changes = ChangesOf(record.Changes),
        };
    }
}

/// <summary>One field of a save (TF-062): its name, its old and new values as kept, and how they are kept.</summary>
public sealed record FieldChangeResponse(string Field, string? Old, string? New, string Treatment);

internal static class AuditEventsEndpoints
{
    /// <summary>The query parameter that finds the events of one client's address, beside the list grammar's filters.</summary>
    public const string AddressKey = "address";

    public const int MaximumAddressLength = 64;

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
        .Add("target", e => e.Target, FieldUse.Select | FieldUse.Filter)
        .Add("record_type", e => e.RecordType, FieldUse.Select | FieldUse.Filter)
        .Add("record_id", e => e.RecordId, FieldUse.Select | FieldUse.Filter)
        .Add("reason", e => e.Reason, FieldUse.Select)
        .Add("correlation_id", e => e.CorrelationId, FieldUse.Select | FieldUse.Filter)
        .Add("details", e => e.Details, FieldUse.Select)
        .Add("created_at", e => e.CreatedAt, FieldUse.Select | FieldUse.Sort | FieldUse.Filter)
        .Add("created_by", e => e.CreatedBy, FieldUse.Select | FieldUse.Filter)
        .Add("chain_position", e => e.ChainPosition, FieldUse.Select | FieldUse.Sort | FieldUse.Filter)
        .SortByDefault("created_at", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var events = endpoints.MapApiGroup("audit-events").RequirePermission(AuditPermissions.EventsRead);
        events.MapGet("/", ListAsync).Produces<ListResponse<AuditEventResponse>>(StatusCodes.Status200OK).WithName("ListAuditEvents").WithSummary("Search the audit trail of this tenant.");
        events.MapGet("/types", Types).Produces<AuditEventTypesResponse>(StatusCodes.Status200OK).WithName("ListAuditEventTypes").WithSummary("Every audit event type this unit's modules write, with the words it reads as.");
        events.MapGet("/impersonators", ImpersonatorsAsync).Produces<SignedInAsListResponse>(StatusCodes.Status200OK).WithName("ListAuditImpersonators").WithSummary("Everyone who signed in as someone, or came in through break-glass, by name: the operators, the break-glass accounts and the tenant's people, each with the field that finds their events.");
    }

    private static IResult Types(AuditEventNames names) =>
        TypedResults.Ok(new AuditEventTypesResponse([.. names.All.Select(t => new AuditEventTypeResponse(t.Code, t.Name))]));

    private static async Task<IResult> ImpersonatorsAsync(AuditImpersonators impersonators, CancellationToken cancellationToken)
    {
        var found = await impersonators.ListAsync(cancellationToken);
        return TypedResults.Ok(new SignedInAsListResponse([.. found.Select(s => new SignedInAsResponse(s.Id, s.Name, s.Kind, s.Field))]));
    }

    private static async Task<IResult> ListAsync(HttpContext http, AuditDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var events = context.Events.AsNoTracking();

        // The client's address is a detail, not a column: a security alert grouped by address links to its events by it (ADR-0056).
        if (http.Request.Query[AddressKey] is [{ } address])
        {
            if (address.Length is 0 or > MaximumAddressLength)
            {
                return ApiResults.Problem(Error.Validation(AuditErrors.AddressInvalid, $"The address filter takes an address of 1 to {MaximumAddressLength} characters.", new FieldError(AddressKey, "invalid", $"Give an address of at most {MaximumAddressLength} characters.")), http);
            }

            var contained = JsonSerializer.Serialize(new Dictionary<string, string> { [AuditDetails.Address] = address });
            events = events.Where(e => e.Details != null && EF.Functions.JsonContains(e.Details, contained));
        }

        var page = await events.ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);

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

        var eventNames = http.RequestServices.GetService<AuditEventNames>();
        return ApiResults.List(page.Map(record => AuditEventResponse.From(record, names) with { EventName = eventNames?.NameOf(record.EventType) ?? record.EventType }), http, query);
    }
}

/// <summary>An audit event type and the words it reads as (ADR-0051).</summary>
public sealed record AuditEventTypeResponse(string Code, string Name);

public sealed record AuditEventTypesResponse(IReadOnlyList<AuditEventTypeResponse> Data);

/// <summary>Someone the "signed in as them by" filter offers, by name, with the list field that finds their events.</summary>
public sealed record SignedInAsResponse(Guid Id, string Name, string Kind, string Field);

public sealed record SignedInAsListResponse(IReadOnlyList<SignedInAsResponse> Data);
