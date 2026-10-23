using Iron.Kernel.History;

namespace Iron.Kernel.Audit.Contracts;

/// <summary>
/// One save of a tracked record as the audit trail keeps it (TF-062, ADR-0051): created, changed or deleted, when, by
/// whom (and who was signed in as them), why, and every field with its old and new value as text.
/// </summary>
public sealed record RecordHistoryEvent(Guid RecordId, DateTimeOffset At, string Kind, IReadOnlyList<FieldChange> Fields, Guid ActorId, Guid? ImpersonatorId, string? Reason);

/// <summary>
/// Reads the field-level history of a record type from the audit trail (ADR-0051), for a module that must say what its
/// records held at a time in the past, such as an evidence pack's state at the period's end (ADR-0052).
/// </summary>
public interface IRecordHistory
{
    /// <summary>The saves of records of this type made in a period, oldest first; an open end reads to now.</summary>
    Task<IReadOnlyList<RecordHistoryEvent>> BetweenAsync(string recordType, DateTimeOffset from, DateTimeOffset? to, CancellationToken cancellationToken);

    /// <summary>The time of the oldest save the database still holds, of any type: before it, history is in the archive only.</summary>
    Task<DateTimeOffset?> OnlineSinceAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Since when the trail keeps field-level history (T7, ADR-0051): the first event sealed in a form that carries field
    /// changes, when the trail holds an event from before it; null when the trail kept it from its first event, as for a
    /// tenant made after T7. A state rebuilt for a time before it is not reliable, since earlier saves were not recorded
    /// field by field (ADR-0052). Read from the database, so once the oldest events are archived it can only move later.
    /// </summary>
    Task<DateTimeOffset?> KeptSinceAsync(CancellationToken cancellationToken);
}

public static class RecordHistoryExtensions
{
    /// <summary>
    /// The records of a type as they stood at <paramref name="at"/>: their fields now with every save made at or after
    /// that time undone. A record created since is left out; one deleted since comes back as it was.
    /// </summary>
    public static async Task<Dictionary<Guid, Dictionary<string, string?>>> AsOfAsync(this IRecordHistory history, string recordType, IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string?>> current, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        return RecordStates.Rewind(current, await history.BetweenAsync(recordType, at, null, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// A record's state at a time in the past, rebuilt from its state now (ADR-0052): every save made after that time is
/// undone, newest first. A change puts back each field's old value, a creation removes the record, and a deletion brings
/// it back with the values it had. A field kept masked or secret stays as the history keeps it.
/// </summary>
public static class RecordStates
{
    /// <summary>The records as they stood before the saves in <paramref name="after"/>, which are those made after the time wanted.</summary>
    public static Dictionary<Guid, Dictionary<string, string?>> Rewind(IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string?>> current, IReadOnlyList<RecordHistoryEvent> after)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(after);
        var states = current.ToDictionary(pair => pair.Key, pair => new Dictionary<string, string?>(pair.Value, StringComparer.Ordinal));
        foreach (var save in after.OrderByDescending(e => e.At))
        {
            switch (save.Kind)
            {
                case RecordChangeKinds.Created:
                    states.Remove(save.RecordId);
                    break;
                case RecordChangeKinds.Deleted:
                    states[save.RecordId] = save.Fields.ToDictionary(f => f.Field, f => f.Old, StringComparer.Ordinal);
                    break;
                default:
                    if (states.TryGetValue(save.RecordId, out var fields))
                    {
                        foreach (var field in save.Fields)
                        {
                            fields[field.Field] = field.Old;
                        }
                    }

                    break;
            }
        }

        return states;
    }
}
