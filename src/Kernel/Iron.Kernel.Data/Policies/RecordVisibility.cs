using System.Linq.Expressions;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Sharing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Data.Policies;

/// <summary>
/// How a reach meets a record type's sharing (TF-222, ADR-0038), as a predicate the database runs and as an answer for
/// one loaded record. A record is visible where the reach covers its owning unit, where the record itself is granted,
/// in tenant mode to anyone with the permission somewhere, and when shared, where the reach covers a listed unit. The
/// shares are a query of the module's own context, so a list joins them in its one query and its total stays right.
/// </summary>
public sealed class RecordVisibility
{
    private readonly IQueryable<RecordShare>? _shares;

    public RecordVisibility(string recordType, IQueryable<RecordShare>? shares)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        RecordType = recordType;
        _shares = shares;
    }

    /// <summary>Without the kernel's shares: a policy block tested on its own, where a shared record is seen by its unit only.</summary>
    public static RecordVisibility WithoutShares { get; } = new("record", null);

    public string RecordType { get; }

    public Expression<Func<T, bool>> Predicate<T>(RecordAccess reach)
        where T : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(reach);
        if (reach.IsEverything)
        {
            return _ => true;
        }

        var units = reach.Units.ToArray();
        var records = reach.Records.ToArray();
        var anywhere = !reach.IsNothing;
        if (_shares is null)
        {
            return record => units.Contains(record.OwningOrganisationId) || records.Contains(record.Id) || (anywhere && record.SharingMode == SharingMode.Tenant);
        }

        var shares = _shares;
        var type = RecordType;
        return record => units.Contains(record.OwningOrganisationId)
            || records.Contains(record.Id)
            || (anywhere && record.SharingMode == SharingMode.Tenant)
            || (record.SharingMode == SharingMode.Shared && shares.Any(s => s.RecordType == type && s.RecordId == record.Id && units.Contains(s.UnitId)));
    }

    /// <summary>
    /// What a list keeps while the reader works for one unit (ADR-0038): records of the working unit and below, records
    /// shared with one of those units, tenant records, and records granted to the reader one by one.
    /// </summary>
    public Expression<Func<T, bool>> Within<T>(IReadOnlyCollection<Guid> workingUnits, RecordAccess reach)
        where T : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(workingUnits);
        ArgumentNullException.ThrowIfNull(reach);
        var working = workingUnits.ToArray();
        var records = reach.Records.ToArray();
        if (_shares is null)
        {
            return record => record.SharingMode == SharingMode.Tenant || working.Contains(record.OwningOrganisationId) || records.Contains(record.Id);
        }

        var shares = _shares;
        var type = RecordType;
        return record => record.SharingMode == SharingMode.Tenant
            || working.Contains(record.OwningOrganisationId)
            || records.Contains(record.Id)
            || (record.SharingMode == SharingMode.Shared && shares.Any(s => s.RecordType == type && s.RecordId == record.Id && working.Contains(s.UnitId)));
    }

    /// <summary>The units a record is listed for; none without the kernel's shares.</summary>
    public async Task<IReadOnlyList<Guid>> SharedUnitsAsync(Guid recordId, CancellationToken cancellationToken)
    {
        if (_shares is null)
        {
            return [];
        }

        var type = RecordType;
        return await _shares.AsNoTracking().Where(s => s.RecordType == type && s.RecordId == recordId).Select(s => s.UnitId).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why the reach sees one loaded record (<see cref="VisibleBy"/>), or null when it does not.</summary>
    public async Task<string?> ReasonAsync<T>(T record, RecordAccess reach, CancellationToken cancellationToken)
        where T : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(reach);
        if (reach.IsEverything || reach.Units.Contains(record.OwningOrganisationId))
        {
            return VisibleBy.Scope;
        }

        if (reach.Records.Contains(record.Id))
        {
            return VisibleBy.Record;
        }

        if (record.SharingMode == SharingMode.Tenant && !reach.IsNothing)
        {
            return VisibleBy.Tenant;
        }

        if (record.SharingMode == SharingMode.Shared && (await SharedUnitsAsync(record.Id, cancellationToken).ConfigureAwait(false)).Any(reach.Units.Contains))
        {
            return VisibleBy.Share;
        }

        return null;
    }
}
