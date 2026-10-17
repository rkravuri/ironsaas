using Iron.Data.Contexts;
using Iron.Data.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Iron.Data;

/// <summary>How a host wires the database: one data source for the unit, then one context per module.</summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the one database connection of this unit (TF-003: a unit holds credentials for its own database only)
    /// and the shared data services. Call once per host. The host also registers an <c>ICurrentPrincipal</c>.
    /// </summary>
    public static IServiceCollection AddIronDatabase(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        services.AddSingleton(dataSource);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AuditStampInterceptor>();
        return services;
    }

    /// <summary>
    /// Registers a module's DbContext against the unit's database, in the module's schema, with snake_case names,
    /// its own migrations history table and the audit stamp interceptor.
    /// </summary>
    public static IServiceCollection AddIronDbContext<TContext>(this IServiceCollection services)
        where TContext : ModuleDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        var schema = SchemaAttribute.For(typeof(TContext));

        services.AddDbContext<TContext>((provider, options) =>
        {
            options.UseNpgsql(
                provider.GetRequiredService<NpgsqlDataSource>(),
                npgsql => npgsql.MigrationsHistoryTable(ModuleDbContext.MigrationsHistoryTable, schema));
            options.UseSnakeCaseNamingConvention();
            options.AddInterceptors(provider.GetRequiredService<AuditStampInterceptor>());
        });

        return services;
    }
}
