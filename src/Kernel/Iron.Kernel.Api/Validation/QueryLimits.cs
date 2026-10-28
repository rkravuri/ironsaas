using Iron.Kernel.Api.Problems;
using Iron.Kernel.Api.Responses;
using Microsoft.AspNetCore.Http;

namespace Iron.Kernel.Api.Validation;

/// <summary>
/// The limits of a request's query (TF-071 AC1, ADR-0053): a body's fields are limited by their validators, and the
/// query's by these, for every endpoint, before a handler or the list parser reads it. Route values are parts of the path,
/// which the server limits. A request over a limit is a validation problem naming the parameter.
/// </summary>
public sealed class QueryLimitMiddleware(RequestDelegate next)
{
    /// <summary>A query parameter's value: room for a list's filters or a search's link, not for a document.</summary>
    public const int ValueMaxLength = 2000;

    /// <summary>The parameters one query may carry.</summary>
    public const int ParametersMaxCount = 50;

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var query = context.Request.Query;
        if (query.Count > ParametersMaxCount)
        {
            return Refuse(context, new FieldError("query", "maximum_count", $"A request carries {ParametersMaxCount} query parameters or fewer."));
        }

        foreach (var (name, values) in query)
        {
            if (name.Length > ValueMaxLength || values.Any(value => value?.Length > ValueMaxLength))
            {
                return Refuse(context, new FieldError(FieldName(name), "maximum_length", $"'{FieldName(name)}' must be {ValueMaxLength} characters or fewer."));
            }
        }

        return next(context);
    }

    private static string FieldName(string name) => name.Length > 100 ? name[..100] : name;

    private static Task Refuse(HttpContext context, FieldError field) =>
        ApiResults.Problem(Error.Validation(ApiErrors.Validation, "The request is not valid.", [field]), context).ExecuteAsync(context);
}
