namespace Iron.Core.Paging;

/// <summary>
/// Which page of a list the caller wants (TF-101). Pages start at 1. A page size above <see cref="MaxPageSize"/>
/// is capped and <see cref="WasCapped"/> is set, so the API can say so in the response.
/// </summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public PageRequest(int pageNumber, int pageSize)
    {
        PageNumber = pageNumber < 1 ? 1 : pageNumber;
        if (pageSize < 1)
        {
            PageSize = DefaultPageSize;
        }
        else if (pageSize > MaxPageSize)
        {
            PageSize = MaxPageSize;
            WasCapped = true;
        }
        else
        {
            PageSize = pageSize;
        }
    }

    public static PageRequest Default => new(1, DefaultPageSize);

    /// <summary>1-based page number.</summary>
    public int PageNumber { get; }

    public int PageSize { get; }

    /// <summary>True when the requested page size was above <see cref="MaxPageSize"/>.</summary>
    public bool WasCapped { get; }

    /// <summary>How many items to skip for this page.</summary>
    public int Skip => (PageNumber - 1) * PageSize;

    /// <summary>Builds a request from optional query values; missing values take the defaults.</summary>
    public static PageRequest From(int? pageNumber, int? pageSize)
        => new(pageNumber ?? 1, pageSize ?? DefaultPageSize);
}
