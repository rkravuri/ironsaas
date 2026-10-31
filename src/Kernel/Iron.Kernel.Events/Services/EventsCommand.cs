using System.Globalization;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Events.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Events.Services;

/// <summary>What the operator asked of the <c>events</c> command, which the fleet puts on the job as <c>Events__Command__*</c>.</summary>
public sealed class EventsCommandOptions
{
    public const string SectionName = "Events:Command";

    /// <summary><c>search</c> or <c>replay</c>.</summary>
    public string? Action { get; set; }

    public string? Type { get; set; }

    public string? RecordId { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary><c>dead</c> for the dead letters only.</summary>
    public string? Status { get; set; }

    /// <summary>The subscription a replay goes to.</summary>
    public string? Subscriber { get; set; }

    /// <summary>The events a replay names, separated by commas.</summary>
    public string? Ids { get; set; }

    public string? FromPosition { get; set; }

    public string? ToPosition { get; set; }

    /// <summary>Why, in the operator's words: a replay needs it.</summary>
    public string? Reason { get; set; }

    /// <summary>Who asked, as the control plane knows them: the tenant's audit trail names them.</summary>
    public string? Operator { get; set; }
}

/// <summary>
/// <c>events</c>: the operator's inspection and replay of a tenant's events (TF-112, ADR-0060), which the fleet runs in the
/// unit when the operator asks the control plane. The control plane puts what was asked on the job, read here from
/// <see cref="EventsCommandOptions"/>: a search by type, record or correlation id, answered as lines of identifiers and
/// states, never data; or a replay to one subscriber, with the operator's name and reason, audited in the tenant's trail as
/// the operator's. The job's output is the answer.
/// </summary>
public sealed class EventsCommand(EventsDbContext context, EventInspection inspection, EventsCommandOptions asked) : IUnitCommand
{
    public const string Search = "search";
    public const string Replay = "replay";

    /// <summary>The most events a search prints.</summary>
    public const int SearchMaximum = 50;

    public string Name => DatabaseCommands.Events;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) => asked.Action switch
    {
        Search => await SearchAsync(cancellationToken).ConfigureAwait(false),
        Replay => await ReplayAsync(cancellationToken).ConfigureAwait(false),
        var other => await RefuseAsync($"events: the action '{other}' is neither {Search} nor {Replay}.").ConfigureAwait(false),
    };

    private async Task<int> SearchAsync(CancellationToken cancellationToken)
    {
        var query = context.Outbox.AsNoTracking();
        if (asked.Type is { Length: > 0 } type)
        {
            query = query.Where(e => e.Type == type);
        }

        if (Guid.TryParse(asked.RecordId, out var recordId))
        {
            query = query.Where(e => e.RecordId == recordId);
        }

        if (asked.CorrelationId is { Length: > 0 } correlation)
        {
            query = query.Where(e => e.CorrelationId == correlation);
        }

        if (asked.Status == Contracts.DeliveryStates.Dead)
        {
            query = query.Where(e => context.Inbox.Any(i => i.EventId == e.Id && i.Status == Contracts.DeliveryStates.Dead));
        }

        var events = await query.OrderByDescending(e => e.Position).Take(SearchMaximum).ToListAsync(cancellationToken).ConfigureAwait(false);
        var deliveries = await inspection.DeliveriesAsync([.. events.Select(e => e.Id)], cancellationToken).ConfigureAwait(false);
        foreach (var found in events)
        {
            var states = deliveries.GetValueOrDefault(found.Id) ?? [];
            var subscribers = states.Count == 0 ? "no subscriber" : string.Join(' ', states.Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Subscriber}={d.Status}({d.Attempts})")));
            await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{found.Position} {found.Id:D} {found.Type} v{found.EventVersion} {found.RecordType}/{found.RecordId:D} {found.OccurredAt:u} {subscribers}")).ConfigureAwait(false);
        }

        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Searched: {events.Count} event(s), newest first, at most {SearchMaximum}.")).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> ReplayAsync(CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        foreach (var id in (asked.Ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(id, out var parsed))
            {
                return await RefuseAsync($"events: '{id}' is not an event's id.").ConfigureAwait(false);
            }

            ids.Add(parsed);
        }

        long? from = long.TryParse(asked.FromPosition, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ? start : null;
        long? to = long.TryParse(asked.ToPosition, NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) ? end : null;
        var request = new ReplayRequest(asked.Subscriber ?? string.Empty, ids, from, to, asked.Reason ?? string.Empty);
        var result = await inspection.ReplayAsync(request, asked.Operator ?? "the platform operator", cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return await RefuseAsync($"events: {result.Error.Code}: {result.Error.Message}").ConfigureAwait(false);
        }

        var done = result.Value;
        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Replayed {done.Events} event(s) to {done.Subscriber}: {done.Requeued} put back to pending, {done.Added} given for the first time, {done.AlreadyHandled} handled already and left as they were.")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RefuseAsync(string message)
    {
        await Console.Error.WriteLineAsync(message).ConfigureAwait(false);
        return 1;
    }
}
