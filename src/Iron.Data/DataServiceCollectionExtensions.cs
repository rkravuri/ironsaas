using Iron.Core.Principals;
using Iron.Data.Commands;
using Iron.Data.Contexts;
using Iron.Data.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iron.Data;

/// <summary>How a host wires the database: one data source for the unit, then one context per module.</summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the one database connection of this unit (TF-003: a unit holds credentials for its own database only)
    /// and the shared data services. Call once per host. A host that serves people registers its request
    /// <c>ICurrentPrincipal</c> as well; without one, the system principal acts.
    /// </summary>
    public static IServiceCollection AddIronDatabase(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        services.AddSingleton(dataSource);
        services.AddLogging(); // the runners and the health check log; a host already has logging, a bare container gets a silent one
        services.TryAddSingleton(TimeProvider.System);

        // Until a host registers the request principal (Identity, T10), the system principal acts. Workers keep it.
        services.TryAddSingleton<ICurrentPrincipal>(SystemPrincipal.Instance);

        services.AddScoped<AuditStampInterceptor>();
        services.AddScoped<MigrationRunner>();
        services.AddScoped<SeedRunner>();
        services.AddScoped<SchemaCheckRunner>();
        return services;
    }

    /// <summary>
    /// Registers a module's DbContext against the unit's database, in the module's schema, with snake_case names,
    /// its own migrations history table and the audit stamp interceptor.
    /// </summary>
    public static IServiceCollection AddIronDbContext<TContext>(this IServiceCollection services, Action<DbContextOptionsBuilder>? configure = null)
        where TContext : ModuleDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        var schema = SchemaAttribute.For(typeof(TContext));

        services.AddSingleton(new RegisteredContext(typeof(TContext), schema));
        services.AddDbContext<TContext>((provider, options) =>
        {
            DesignTime.Configure<TContext>(options, provider.GetRequiredService<NpgsqlDataSource>());
            options.AddInterceptors(provider.GetRequiredService<AuditStampInterceptor>());
            configure?.Invoke(options);
        });

        return services;
    }
}
