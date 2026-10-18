using Iron.Kernel;
using Iron.Telemetry.Correlation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Api.Problems;

/// <summary>
/// The last line: any exception the endpoint did not handle becomes a 500 problem with a correlation id and no
/// internal details (TF-100, TF-130). The exception itself goes to the log with the same id.
/// </summary>
public sealed class UnexpectedExceptionHandler(ILogger<UnexpectedExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var correlationId = RequestCorrelation.Get(httpContext).Value;
        logger.LogError(exception, "Unhandled exception. correlation_id={CorrelationId} path={Path}", correlationId, httpContext.Request.Path);

        var error = Error.Unexpected(ApiErrors.Unexpected, "Something went wrong. Quote the correlation id when you ask for help.");
        var problem = ProblemFactory.Create(error, httpContext);
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        var problemDetailsService = httpContext.RequestServices.GetService<IProblemDetailsService>();
        if (problemDetailsService is not null
            && await problemDetailsService.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem, Exception = exception }))
        {
            return true;
        }

        await httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken);
        return true;
    }
}
