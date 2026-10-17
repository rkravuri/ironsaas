namespace Iron.Core.Paging;

/// <summary>One page of a list with the total count, as every list operation returns it (TF-100).</summary>
public sealed class Page<T>
{
    public Page(IReadOnlyList<T> items, PageRequest request, long totalCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);

        Items = items;
        PageNumber = request.PageNumber;
        PageSize = request.PageSize;
        TotalCount = totalCount;
        WasCapped = request.WasCapped;
    }

    private Page(IReadOnlyList<T> items, int pageNumber, int pageSize, long totalCount, bool wasCapped)
    {
        Items = items;
        PageNumber = pageNumber;
        PageSize = pageSize;
        TotalCount = totalCount;
        WasCapped = wasCapped;
    }

    public IReadOnlyList<T> Items { get; }

    public int PageNumber { get; }

    public int PageSize { get; }

    public long TotalCount { get; }

    public bool WasCapped { get; }

    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPrevious => PageNumber > 1;

    public bool HasNext => PageNumber < TotalPages;

    public static Page<T> Empty(PageRequest request) => new([], request, 0);

    /// <summary>The same page with every item converted, for example from an entity to its API shape.</summary>
    public Page<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var items = new List<TOut>(Items.Count);
        foreach (var item in Items)
        {
            items.Add(map(item));
        }

        return new Page<TOut>(items, PageNumber, PageSize, TotalCount, WasCapped);
    }
}
