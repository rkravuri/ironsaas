using Iron.Kernel.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Data.History;

/// <summary>
/// Tracked records' fields now, by id, as their history writes them, with the record type their history uses (ADR-0051):
/// what a module hands the Audit module's <c>RecordStates</c> to rebuild its records as they stood at a past time (ADR-0052).
/// </summary>
public static class TrackedFields
{
    public static (string RecordType, IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string?>> Records) Of<T>(DbContext context, IEnumerable<T> entities)
        where T : Entity, ITrackedChanges
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);
        var type = context.Model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} is not in this context.");
        var records = new Dictionary<Guid, IReadOnlyDictionary<string, string?>>();
        foreach (var entity in entities)
        {
            records[entity.Id] = ChangeHistoryInterceptor.Fields(context.Entry(entity));
        }

        return (ChangeHistoryInterceptor.RecordTypeOf(type), records);
    }
}
