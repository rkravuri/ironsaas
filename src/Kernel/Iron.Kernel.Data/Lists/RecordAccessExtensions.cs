using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Data.Lists;

public static class RecordAccessExtensions
{
    /// <summary>
    /// Keeps the records one permission reaches (ADR-0030): all of them for a tenant-wide grant, otherwise those owned by
    /// one of the units or granted one by one. One predicate in the list's own query (<c>= any(...)</c>), whatever the
    /// number of rows; the access itself comes from <c>IRecordAccess.AccessibleAsync</c> once per list.
    /// </summary>
    public static IQueryable<TRecord> WhereAccessible<TRecord>(this IQueryable<TRecord> query, RecordAccess access)
        where TRecord : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(access);
        if (access.IsEverything)
        {
            return query;
        }

        var units = access.Units.ToArray();
        var records = access.Records.ToArray();
        return query.Where(record => units.Contains(record.OwningOrganisationId) || records.Contains(record.Id));
    }
}
