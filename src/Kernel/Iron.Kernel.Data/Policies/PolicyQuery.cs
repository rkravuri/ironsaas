using System.Linq.Expressions;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Lists;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Data.Policies;

/// <summary>
/// A query of business records with the scope rule and the record type's policy already in it (ADR-0032). Only
/// <see cref="IRecordPolicies"/> makes one, and it never hands the raw query back: it narrows further, then pages,
/// counts or lists. Paging a raw query of a business record does not compile, so a list cannot forget its policy. What
/// it reads is audited once per page when it holds private records of other units (ADR-0038).
/// </summary>
public sealed class PolicyQuery<T>
    where T : BusinessRecord
{
    private readonly Func<IReadOnlyList<T>, CancellationToken, Task>? _read;

    internal PolicyQuery(IQueryable<T> query, Func<IReadOnlyList<T>, CancellationToken, Task>? read = null)
    {
        Query = query;
        _read = read;
    }

    internal IQueryable<T> Query { get; }

    /// <summary>Narrows the records further; a condition can only take records away.</summary>
    public PolicyQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new PolicyQuery<T>(Query.Where(predicate), _read);
    }

    /// <summary>The list's filters and sorts (TF-101), in the same query as the policy, so the total counts what the reader may see.</summary>
    public PolicyQuery<T> ApplyListQuery(ListQuery query, ListFields<T> fields) => new(Query.ApplyListQuery(query, fields), _read);

    public PolicyQuery<T> AsNoTracking() => new(Query.AsNoTracking(), _read);

    /// <summary>One page with its total count: both counted under the policy (TF-035 AC2), and the page audited as one read.</summary>
    public async Task<Page<T>> ToPageAsync(PageRequest page, CancellationToken cancellationToken)
    {
        var result = await ListQueryExtensions.PageAsync(Query, page, cancellationToken).ConfigureAwait(false);
        await ReadAsync(result.Items, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<List<T>> ToListAsync(CancellationToken cancellationToken)
    {
        var result = await Query.ToListAsync(cancellationToken).ConfigureAwait(false);
        await ReadAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<int> CountAsync(CancellationToken cancellationToken) => Query.CountAsync(cancellationToken);

    private Task ReadAsync(IReadOnlyList<T> items, CancellationToken cancellationToken) => _read is null || items.Count == 0 ? Task.CompletedTask : _read(items, cancellationToken);
}
