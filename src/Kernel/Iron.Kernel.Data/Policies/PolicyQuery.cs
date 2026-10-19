using System.Linq.Expressions;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Lists;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Data.Policies;

/// <summary>
/// A query of business records with the scope rule and the record type's policy already in it (ADR-0032). Only
/// <see cref="IRecordPolicies"/> makes one, and it never hands the raw query back: it narrows further, then pages,
/// counts or lists. Paging a raw query of a business record does not compile, so a list cannot forget its policy.
/// </summary>
public sealed class PolicyQuery<T>
    where T : BusinessRecord
{
    internal PolicyQuery(IQueryable<T> query)
    {
        Query = query;
    }

    internal IQueryable<T> Query { get; }

    /// <summary>Narrows the records further; a condition can only take records away.</summary>
    public PolicyQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new PolicyQuery<T>(Query.Where(predicate));
    }

    /// <summary>The list's filters and sorts (TF-101), in the same query as the policy, so the total counts what the reader may see.</summary>
    public PolicyQuery<T> ApplyListQuery(ListQuery query, ListFields<T> fields) => new(Query.ApplyListQuery(query, fields));

    public PolicyQuery<T> AsNoTracking() => new(Query.AsNoTracking());

    /// <summary>One page with its total count: both counted under the policy (TF-035 AC2).</summary>
    public Task<Page<T>> ToPageAsync(PageRequest page, CancellationToken cancellationToken) => ListQueryExtensions.PageAsync(Query, page, cancellationToken);

    public Task<List<T>> ToListAsync(CancellationToken cancellationToken) => Query.ToListAsync(cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken) => Query.CountAsync(cancellationToken);
}
