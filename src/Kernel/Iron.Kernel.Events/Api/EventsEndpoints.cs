using System.Text.Json;
using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Events.Data;
using Iron.Kernel.Events.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Events.Api;

public sealed record EventDeliveryResponse(string Subscriber, string Status, int Attempts, DateTimeOffset NextAttemptAt, string? LastError, DateTimeOffset? HandledAt, DateTimeOffset? DeadAt, int Replays, DateTimeOffset? HeldAt);

/// <summary>An event as the console shows it: what it is, about which record, who acted, when, its data, and where it stands with each subscriber.</summary>
public sealed record EventResponse(
    Guid Id,
    long Position,
    string Type,
    int Version,
    string RecordType,
    Guid RecordId,
    Guid ActorId,
    Guid? OnBehalfOfId,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    JsonElement Data,
    string Status,
    IReadOnlyList<EventDeliveryResponse> Deliveries);

/// <summary>An event a subscriber parked, with the error of its last attempt.</summary>
public sealed record DeadLetterResponse(Guid EventId, long Position, string Subscriber, string Type, int Version, string RecordType, Guid RecordId, DateTimeOffset OccurredAt, int Attempts, string? LastError, DateTimeOffset? DeadAt);

public sealed record EventSubscriberResponse(string Name, string Module, string Description);

/// <summary>A declared event (TF-110): its contract, and the subscriptions of this unit that read it.</summary>
public sealed record EventTypeResponse(string Name, int Version, string Module, string RecordType, string Description, string Owner, IReadOnlyList<EventField> Fields, DateOnly? DeprecatedOn, int? ReplacedBy, IReadOnlyList<EventSubscriberResponse> Subscribers);

public sealed record EventRetentionResponse(int Days, DateTimeOffset? ArchivedThrough, int Archives);

/// <summary>A replay to one subscriber: these events, or every event it reads between two positions, with why.</summary>
public sealed record ReplayEventsRequest(string Subscriber, IReadOnlyList<Guid>? EventIds, long? FromPosition, long? ToPosition, string Reason);

public sealed record ReplayEventsResponse(string Subscriber, int Events, int Requeued, int AlreadyHandled, int Added);

public sealed class ReplayEventsRequestValidator : AbstractValidator<ReplayEventsRequest>
{
    public ReplayEventsRequestValidator()
    {
        RuleFor(r => r.Subscriber).NotEmpty().MaximumLength(EventSubscription.MaxNameLength);
        RuleFor(r => r.EventIds).MaximumCount(EventInspection.ReplayMaximum);
        RuleFor(r => r.FromPosition).GreaterThanOrEqualTo(1);
        RuleFor(r => r.ToPosition).GreaterThanOrEqualTo(1);
        RuleFor(r => r.Reason).NotEmpty().MaximumLength(EventInspection.ReasonMaxLength);
    }
}

/// <summary>
/// The events of the unit (TF-110 to TF-112, ADR-0060): searched by type, record, time and correlation id, each with
/// where it stands with each subscriber; the dead letters; the declared events with their subscribers; the retention; and
/// a replay to one subscriber with a reason and a recent sign-in, audited.
/// </summary>
internal static class EventsEndpoints
{
    public static readonly ListFields<OutboxEvent> Fields = new ListFields<OutboxEvent>()
        .Add("id", e => e.Id, FieldUse.Select | FieldUse.Filter)
        .Add("position", e => e.Position, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("type", e => e.Type, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("version", e => e.EventVersion, FieldUse.Select | FieldUse.Filter)
        .Add("record_type", e => e.RecordType, FieldUse.Select | FieldUse.Filter)
        .Add("record_id", e => e.RecordId, FieldUse.Select | FieldUse.Filter)
        .Add("actor_id", e => e.ActorId, FieldUse.Select | FieldUse.Filter)
        .Add("occurred_at", e => e.OccurredAt, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("correlation_id", e => e.CorrelationId, FieldUse.Select | FieldUse.Filter)
        .SortByDefault("position", SortDirection.Descending);

    public static readonly ListFields<InboxEntry> DeadFields = new ListFields<InboxEntry>()
        .Add("subscriber", i => i.Subscriber, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("event_id", i => i.EventId, FieldUse.Select | FieldUse.Filter)
        .Add("record_id", i => i.RecordId, FieldUse.Select | FieldUse.Filter)
        .Add("position", i => i.Position, FieldUse.Select | FieldUse.Sort)
        .Add("dead_at", i => i.DeadAt, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .SortByDefault("dead_at", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var events = endpoints.MapApiGroup("events");
        events.MapGet("/", ListAsync).RequirePermission(EventsPermissions.EventsRead).Produces<ListResponse<EventResponse>>(StatusCodes.Status200OK).WithName("ListEvents").WithSummary("The domain events, newest first, by type, record, time and correlation id, each with its subscribers.");
        events.MapGet("/{id:guid}", GetAsync).RequirePermission(EventsPermissions.EventsRead).Produces<EventResponse>(StatusCodes.Status200OK).WithName("GetEvent").WithSummary("One event with where it stands with each subscriber, or that it was archived.");
        events.MapGet("/dead-letters", DeadLettersAsync).RequirePermission(EventsPermissions.EventsRead).Produces<ListResponse<DeadLetterResponse>>(StatusCodes.Status200OK).WithName("ListDeadLetters").WithSummary("The events a subscriber parked after its last attempt, with the error.");
        events.MapGet("/types", TypesAsync).RequirePermission(EventsPermissions.EventsRead).Produces<IReadOnlyList<EventTypeResponse>>(StatusCodes.Status200OK).WithName("ListEventTypes").WithSummary("The events this unit's modules publish, with their fields and the subscriptions that read them.");
        events.MapGet("/retention", RetentionAsync).RequirePermission(EventsPermissions.EventsRead).Produces<EventRetentionResponse>(StatusCodes.Status200OK).WithName("GetEventRetention").WithSummary("How long handled events stay searchable, and up to when they were archived.");
        events.MapPost("/replay", ReplayAsync).RequirePermission(EventsPermissions.EventsReplay).RequireRecentAuthentication().Validate<ReplayEventsRequest>().Produces<ReplayEventsResponse>(StatusCodes.Status200OK).WithName("ReplayEvents").WithSummary("Replay events to one subscriber, with a reason: dead letters run again, handled events stay handled.");
    }

    private static async Task<IResult> ListAsync(HttpContext http, EventsDbContext context, EventInspection inspection, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.Outbox.AsNoTracking().ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        var deliveries = await inspection.DeliveriesAsync([.. page.Items.Select(e => e.Id)], cancellationToken);
        return ApiResults.List(page.Map(e => Response(new EventView(e, deliveries.GetValueOrDefault(e.Id) ?? []))), http, query);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, EventInspection inspection, CancellationToken cancellationToken)
    {
        var result = await inspection.GetAsync(id, cancellationToken);
        return ApiResults.From(result, http, view => TypedResults.Ok(Response(view)));
    }

    private static async Task<IResult> DeadLettersAsync(HttpContext http, EventsDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, DeadFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.Inbox.AsNoTracking().Where(i => i.Status == DeliveryStates.Dead).ApplyListQuery(query, DeadFields).ToPageAsync(query.Page, cancellationToken);
        var ids = page.Items.Select(i => i.EventId).ToList();
        var events = await context.Outbox.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        return ApiResults.List(page.Map(i => events.TryGetValue(i.EventId, out var e)
            ? new DeadLetterResponse(i.EventId, i.Position, i.Subscriber, e.Type, e.EventVersion, e.RecordType, e.RecordId, e.OccurredAt, i.Attempts, i.LastError, i.DeadAt)
            : new DeadLetterResponse(i.EventId, i.Position, i.Subscriber, "unknown", 0, i.RecordType, i.RecordId, i.CreatedAt, i.Attempts, i.LastError, i.DeadAt)), http, query);
    }

    private static IResult TypesAsync(EventDeclarations declarations) =>
        TypedResults.Ok<IReadOnlyList<EventTypeResponse>>([.. declarations.Events.Select(type => new EventTypeResponse(
            type.Name,
            type.Version,
            type.Module,
            type.RecordType,
            type.Description,
            type.Owner,
            type.Fields,
            type.DeprecatedOn,
            type.ReplacedBy,
            [.. declarations.SubscribersOf(type.Name, type.Version).Select(s => new EventSubscriberResponse(s.Name, s.Module, s.Description))]))]);

    private static async Task<IResult> RetentionAsync(EventInspection inspection, CancellationToken cancellationToken)
    {
        var view = await inspection.RetentionAsync(cancellationToken);
        return TypedResults.Ok(new EventRetentionResponse(view.Days, view.ArchivedThrough, view.Archives));
    }

    private static async Task<IResult> ReplayAsync(ReplayEventsRequest request, HttpContext http, EventInspection inspection, CancellationToken cancellationToken)
    {
        var result = await inspection.ReplayAsync(new ReplayRequest(request.Subscriber, request.EventIds, request.FromPosition, request.ToPosition, request.Reason), null, cancellationToken);
        return ApiResults.From(result, http, done => TypedResults.Ok(new ReplayEventsResponse(done.Subscriber, done.Events, done.Requeued, done.AlreadyHandled, done.Added)));
    }

    private static EventResponse Response(EventView view)
    {
        var e = view.Event;
        using var data = JsonDocument.Parse(e.Data);
        return new EventResponse(
            e.Id,
            e.Position,
            e.Type,
            e.EventVersion,
            e.RecordType,
            e.RecordId,
            e.ActorId,
            e.OnBehalfOfId,
            e.OccurredAt,
            e.CorrelationId,
            data.RootElement.Clone(),
            view.Status,
            [.. view.Deliveries.Select(d => new EventDeliveryResponse(d.Subscriber, d.Status, d.Attempts, d.NextAttemptAt, d.LastError, d.HandledAt, d.DeadAt, d.Replays, d.HeldAt))]);
    }
}
