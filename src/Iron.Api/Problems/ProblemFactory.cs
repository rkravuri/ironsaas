using Iron.Core.Correlation;
using Iron.Core.Results;
using Iron.Telemetry.Correlation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Iron.Api.Problems;

/// <summary>One field problem as the client sees it inside a problem response.</summary>
public sealed record FieldProblem(string Field, string Code, string Message);

/// <summary>
/// Turns an <see cref="Error"/> into RFC 9457 problem details (TF-100): type, title, status, detail, instance, plus the
/// extensions <c>error_code</c>, <c>correlation_id</c> and, for validation, <c>errors</c>.
/// </summary>
public static class ProblemFactory
{
    public const string TypePrefix = "urn:iron:error:";
    public const string ErrorCodeKey = "error_code";
    public const string CorrelationIdKey = "correlation_id";
    public const string ErrorsKey = "errors";

    public static int StatusFor(ErrorKind kind)
    {
        return kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };
    }

    public static string TitleFor(ErrorKind kind)
    {
        return kind switch
        {
            ErrorKind.Validation => "The request is not valid.",
            ErrorKind.Unauthorized => "Authentication is required.",
            ErrorKind.Forbidden => "This action is not allowed.",
            ErrorKind.NotFound => "Not found.",
            ErrorKind.Conflict => "The request conflicts with the current state.",
            _ => "Something went wrong.",
        };
    }

    public static ProblemDetails Create(Error error, HttpContext? context)
    {
        ArgumentNullException.ThrowIfNull(error);

        var problem = new ProblemDetails
        {
            Type = TypePrefix + error.Code,
            Title = TitleFor(error.Kind),
            Status = StatusFor(error.Kind),
            Detail = error.Message,
            Instance = context?.Request.Path.Value,
        };
        problem.Extensions[ErrorCodeKey] = error.Code;
        problem.Extensions[CorrelationIdKey] = (context is null ? CorrelationId.Current() : RequestCorrelation.Get(context)).Value;
        if (error.Fields.Count > 0)
        {
            problem.Extensions[ErrorsKey] = error.Fields.Select(f => new FieldProblem(f.Field, f.Code, f.Message)).ToArray();
        }

        return problem;
    }
}
