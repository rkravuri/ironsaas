using Iron.Kernel.Files.Contracts;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iron.Kernel.Files.Services;

/// <summary>Readiness check (TF-133): the tenant's container exists and storage answers. The message names what is missing.</summary>
public sealed class ObjectStorageHealthCheck(IObjectStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var problem = await storage.CheckAsync(cancellationToken).ConfigureAwait(false);
            return problem is null ? HealthCheckResult.Healthy("Object storage answers and the container exists.") : HealthCheckResult.Unhealthy(problem);
        }
        catch (InvalidOperationException exception)
        {
            return HealthCheckResult.Unhealthy(exception.Message);
        }
    }
}
