using System.Linq.Expressions;
using Iron.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Iron.Data.Lists;

/// <summary>
/// Applies a parsed <see cref="ListQuery"/> to a query (TF-101): filters, then sorts, then one page with its total.
/// Only fields from the resource's <see cref="ListFields{T}"/> are used; the API parser has already refused the rest.
/// </summary>
public static class ListQueryExtensions
{
    public static IQueryable<T> ApplyListQuery<T>(this IQueryable<T> source, ListQuery query, ListFields<T> fields)
    {
        ArgumentNullException.ThrowIfNull(query);
        return source.ApplyFilters(query.Filters, fields).ApplySorts(query.Sorts, fields);
    }

    public static IQueryable<T> ApplyFilters<T>(this IQueryable<T> source, IReadOnlyList<FilterTerm> filters, ListFields<T> fields)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(fields);

        foreach (var term in filters)
        {
            var field = fields.Get(term.Field) ?? throw new ArgumentException($"'{term.Field}' is not a field of this list.", nameof(filters));
            source = source.Where(FilterExpressions.Build(field, term));
        }

        return source;
    }

    /// <summary>Sorts by the given terms, or by the list's default sort, so that paging is always stable.</summary>
    public static IQueryable<T> ApplySorts<T>(this IQueryable<T> source, IReadOnlyList<SortTerm> sorts, ListFields<T> fields)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sorts);
        ArgumentNullException.ThrowIfNull(fields);

        IReadOnlyList<SortTerm> terms = sorts;
        if (terms.Count == 0 && fields.DefaultSort is { } defaultSort)
        {
            terms = new[] { defaultSort };
        }

        IOrderedQueryable<T>? ordered = null;
        foreach (var term in terms)
        {
            var field = fields.Get(term.Field) ?? throw new ArgumentException($"'{term.Field}' is not a field of this list.", nameof(sorts));
            ordered = ordered is null
                ? Order(source, field, term.Direction, first: true)
                : Order(ordered, field, term.Direction, first: false);
        }

        return ordered ?? source;
    }

    /// <summary>One page of the query with its total count, for the list envelope.</summary>
    public static async Task<Page<T>> ToPageAsync<T>(this IQueryable<T> source, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(page);

        var total = await source.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await source.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new Page<T>(items, page, total);
    }

    private static IOrderedQueryable<T> Order<T>(IQueryable<T> source, ListField<T> field, SortDirection direction, bool first)
    {
        var method = (first, direction) switch
        {
            (true, SortDirection.Ascending) => nameof(Queryable.OrderBy),
            (true, SortDirection.Descending) => nameof(Queryable.OrderByDescending),
            (false, SortDirection.Ascending) => nameof(Queryable.ThenBy),
            _ => nameof(Queryable.ThenByDescending),
        };

        var call = Expression.Call(typeof(Queryable), method, new[] { typeof(T), field.ValueType }, source.Expression, Expression.Quote(field.Selector));
        return (IOrderedQueryable<T>)source.Provider.CreateQuery<T>(call);
    }
}
