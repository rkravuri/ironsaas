using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iron.Kernel.Telemetry.Health;

/// <summary>
/// The two probes every host answers. <c>/health/live</c> says the process runs. <c>/health/ready</c> runs every check
/// tagged <c>ready</c> (database, storage, secrets) and answers 503 until all of them pass. Both are on the anonymous
/// allow-list (TF-030). The body is JSON so people and tools can read which check failed.
/// </summary>
public static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    public const string ReadyTag = "ready";

    public static IServiceCollection AddIronHealth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHealthChecks();
        return services;
    }

    public static IEndpointRouteBuilder MapIronHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync }).AllowAnonymous();
        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag), ResponseWriter = WriteAsync }).AllowAnonymous();
        return endpoints;
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var body = new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            total_duration_ms = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString().ToLowerInvariant(),
                duration_ms = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                description = entry.Value.Description,
                error = entry.Value.Exception?.Message,
            }).ToArray(),
        };

        return context.Response.WriteAsJsonAsync(body, context.RequestAborted);
    }
}
