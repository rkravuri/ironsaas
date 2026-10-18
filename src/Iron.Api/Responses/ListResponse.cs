using System.Globalization;
using Iron.Kernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;

namespace Iron.Api.Responses;

/// <summary>The envelope of every list operation (TF-100): the items, the paging facts and the navigation links.</summary>
public sealed record ListResponse<T>(IReadOnlyList<T> Data, ListMeta Meta, ListLinks Links);

public sealed record ListMeta(
    int PageNumber,
    int PageSize,
    long TotalCount,
    int TotalPages,
    bool HasPrevious,
    bool HasNext,
    bool PageSizeCapped,
    int MaxPageSize)
{
    public static ListMeta For<T>(Page<T> page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new ListMeta(page.PageNumber, page.PageSize, page.TotalCount, page.TotalPages, page.HasPrevious, page.HasNext, page.WasCapped, PageRequest.MaxPageSize);
    }
}

public sealed record ListLinks(string Self, string First, string? Previous, string? Next, string Last)
{
    /// <summary>Links keep every other query parameter of the request and change only the page number.</summary>
    public static ListLinks For<T>(HttpRequest request, Page<T> page)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(page);

        var lastPage = Math.Max(page.TotalPages, 1);
        return new ListLinks(
            Link(request, page.PageNumber),
            Link(request, 1),
            page.HasPrevious ? Link(request, page.PageNumber - 1) : null,
            page.HasNext ? Link(request, page.PageNumber + 1) : null,
            Link(request, lastPage));
    }

    private static string Link(HttpRequest request, int pageNumber)
    {
        var builder = new QueryBuilder();
        foreach (var (key, values) in request.Query)
        {
            if (string.Equals(key, Lists.ListQueryParser.PageNumberKey, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var value in values)
            {
                if (value is not null)
                {
                    builder.Add(key, value);
                }
            }
        }

        builder.Add(Lists.ListQueryParser.PageNumberKey, pageNumber.ToString(CultureInfo.InvariantCulture));
        return request.PathBase.Add(request.Path).Value + builder.ToQueryString().Value;
    }
}
