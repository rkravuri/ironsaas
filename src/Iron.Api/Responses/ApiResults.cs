using Iron.Api.Lists;
using Iron.Api.Problems;
using Iron.Core.Paging;
using Iron.Core.Results;
using Microsoft.AspNetCore.Http;

namespace Iron.Api.Responses;

/// <summary>How endpoints turn results into responses. Every failure goes through <see cref="Problem"/>.</summary>
public static class ApiResults
{
    public static IResult Problem(Error error, HttpContext context)
    {
        return TypedResults.Problem(ProblemFactory.Create(error, context));
    }

    /// <summary>A value becomes the given success response; an error becomes a problem.</summary>
    public static IResult From<T>(Result<T> result, HttpContext context, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : Problem(result.Error, context);
    }

    public static IResult From(Result result, HttpContext context, Func<IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess() : Problem(result.Error, context);
    }

    /// <summary>A page in the list envelope. When the query selected fields, every item is cut down to them.</summary>
    public static IResult List<T>(Page<T> page, HttpContext context, ListQuery? query = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(context);

        var meta = ListMeta.For(page);
        var links = ListLinks.For(context.Request, page);
        if (query is { Fields.Count: > 0 })
        {
            return TypedResults.Ok(new ListResponse<IReadOnlyDictionary<string, object?>>(FieldShaper.Shape(page.Items, query.Fields), meta, links));
        }

        return TypedResults.Ok(new ListResponse<T>(page.Items, meta, links));
    }
}
