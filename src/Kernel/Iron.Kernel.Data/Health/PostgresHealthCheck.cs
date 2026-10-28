using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Iron.Kernel.Data.Health;

/// <summary>
/// Readiness check: the unit's database answers a trivial query, on a connection opened as requests open theirs, so a
/// password being rotated does not mark the unit unready (ADR-0054).
/// </summary>
public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource, IDatabaseCredentials credentials) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = dataSource.CreateConnection();
            await Transactions.DatabaseConnections.OpenAsync(connection, credentials, cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("select 1", connection);
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("PostgreSQL answers.");
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL does not answer.", exception);
        }
    }
}

public static class DataHealthExtensions
{
    /// <summary>Adds the database to the readiness probe. Needs AddIronDatabase first.</summary>
    public static IServiceCollection AddIronDatabaseHealthCheck(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        return services;
    }
}
