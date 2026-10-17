using Iron.Api.Json;
using Iron.Api.Problems;
using Iron.Core.Correlation;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Api;

/// <summary>How a host switches on the REST conventions: AddIronApi in services, UseIronApi first in the pipeline.</summary>
public static class IronApiExtensions
{
    public static IServiceCollection AddIronApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.ConfigureHttpJsonOptions(options => IronJson.Configure(options.SerializerOptions));

        // Problems the framework makes on its own (bad JSON, missing route, wrong method) get the same extensions as ours.
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;
            var status = problem.Status ?? context.HttpContext.Response.StatusCode;
            problem.Extensions.TryAdd(ProblemFactory.ErrorCodeKey, ApiErrors.ForStatus(status));
            problem.Extensions.TryAdd(ProblemFactory.CorrelationIdKey, CorrelationId.Current().Value);
            if (problem.Type is null || !problem.Type.StartsWith(ProblemFactory.TypePrefix, StringComparison.Ordinal))
            {
                problem.Type = ProblemFactory.TypePrefix + problem.Extensions[ProblemFactory.ErrorCodeKey];
            }

            problem.Instance ??= context.HttpContext.Request.Path.Value;
        });

        services.AddExceptionHandler<UnexpectedExceptionHandler>();
        return services;
    }

    /// <summary>Exception handling and status code problems. Call before authentication and endpoints.</summary>
    public static IApplicationBuilder UseIronApi(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }
}
