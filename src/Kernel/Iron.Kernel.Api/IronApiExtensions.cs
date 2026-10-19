using Iron.Kernel.Api.Json;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Problems;
using Iron.Kernel.Telemetry.Correlation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Api;

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
            problem.Extensions.TryAdd(ProblemFactory.CorrelationIdKey, RequestCorrelation.Get(context.HttpContext).Value);
            if (problem.Type is null || !problem.Type.StartsWith(ProblemFactory.TypePrefix, StringComparison.Ordinal))
            {
                problem.Type = ProblemFactory.TypePrefix + problem.Extensions[ProblemFactory.ErrorCodeKey];
            }

            problem.Instance ??= context.HttpContext.Request.Path.Value;
            // The framework adds the W3C trace id under the JSON naming policy's key; the correlation id already is the trace id.
            problem.Extensions.Remove("trace_id");
            problem.Extensions.Remove("traceId");
        });

        services.AddExceptionHandler<UnexpectedExceptionHandler>();
        services.AddTransient<IStartupFilter, EndpointPermissionCheck>();
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
