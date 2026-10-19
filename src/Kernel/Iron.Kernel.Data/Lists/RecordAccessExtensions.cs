using System.Linq.Expressions;
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
        return access.IsEverything ? query : query.Where(Predicate<TRecord>(access));
    }

    /// <summary>The same rule as a predicate, for a record policy to join with its own (ADR-0032).</summary>
    public static Expression<Func<TRecord, bool>> Predicate<TRecord>(RecordAccess access)
        where TRecord : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.IsEverything)
        {
            return _ => true;
        }

        var units = access.Units.ToArray();
        var records = access.Records.ToArray();
        return record => units.Contains(record.OwningOrganisationId) || records.Contains(record.Id);
    }

    /// <summary>
    /// The same rule for rows that are not business records but belong to a unit another way, such as a principal and its
    /// home unit (ADR-0031): <paramref name="owningUnit"/> names the unit, <paramref name="id"/> the row's id. A row
    /// without a unit is reached only by a tenant-wide grant or a grant of that row.
    /// </summary>
    public static IQueryable<TRow> WhereAccessible<TRow>(this IQueryable<TRow> query, RecordAccess access, Expression<Func<TRow, Guid?>> owningUnit, Expression<Func<TRow, Guid>> id)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(owningUnit);
        ArgumentNullException.ThrowIfNull(id);
        if (access.IsEverything)
        {
            return query;
        }

        // The values sit in fields of an object, so Entity Framework sends them as parameters rather than inlining them.
        var values = Expression.Constant(new AccessValues(access.Units.Select(unit => (Guid?)unit).ToArray(), access.Records.ToArray()));
        var row = owningUnit.Parameters[0];
        var rowId = new Rebind(id.Parameters[0], row).Visit(id.Body);
        var inUnits = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid?)], Expression.Field(values, nameof(AccessValues.Units)), owningUnit.Body);
        var inRecords = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid)], Expression.Field(values, nameof(AccessValues.Records)), rowId);
        return query.Where(Expression.Lambda<Func<TRow, bool>>(Expression.OrElse(inUnits, inRecords), row));
    }

    private sealed class AccessValues(Guid?[] units, Guid[] records)
    {
        public readonly Guid?[] Units = units;

        public readonly Guid[] Records = records;
    }

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
