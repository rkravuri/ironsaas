using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Conventions;
using Iron.Kernel.Data.History;
using Iron.Kernel.Data.Policies;
using Iron.Kernel.Data.Sharing;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iron.Kernel.Data;

/// <summary>How a host wires the database: one data source for the unit, then one context per module.</summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the one database connection of this unit (TF-003: a unit holds credentials for its own database only)
    /// and the shared data services. Call once per host. A host that serves people registers its request
    /// <c>ICurrentPrincipal</c> as well; without one, the system principal acts. <paramref name="appRole"/> is the
    /// runtime database role whose privileges <c>migrate</c> grants (TF-060 AC2); the fleet configures it per unit, and
    /// <paramref name="auditReaderRole"/>, the audit verifier's, which may only read the chain (ADR-0050).
    /// </summary>
    public static IServiceCollection AddIronDatabase(this IServiceCollection services, string connectionString, string? appRole = null, string? auditReaderRole = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Built by the container, so disposing a provider closes its pooled connections (tests build many providers).
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddLogging(); // the runners and the health check log; a host already has logging, a bare container gets a silent one
        services.TryAddSingleton(TimeProvider.System);

        // Until a host registers the request principal (Identity, T10), the system principal acts. Workers keep it.
        services.TryAddSingleton<ICurrentPrincipal>(SystemPrincipal.Instance);

        // Nobody signs in until Identity is there, so nothing needs a recent sign-in; Identity replaces it (TF-248).
        services.TryAddSingleton<IRecentAuthentication>(NoSignInRecentAuthentication.Instance);

        services.AddSingleton(new DatabaseOptions { AppRole = appRole, AuditReaderRole = auditReaderRole });
        services.AddScoped<SharedConnection>();
        services.AddScoped<UnitOfWork>();
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<UnitOfWork>());
        services.AddScoped<AuditStampInterceptor>();

        // Field-level history (TF-062, ADR-0051): the Audit module supplies the recorder; without it nothing is recorded.
        services.AddScoped<ChangeHistoryInterceptor>();
        services.TryAddScoped<IChangeReason, ChangeReason>();
        services.AddScoped<OwningOrganisationInterceptor>();
        services.AddScoped<MigrationRunner>();
        services.AddScoped<SeedRunner>();
        services.AddScoped<SchemaCheckRunner>();
        services.AddScoped<GrantsRunner>();

        // Lists and reads by id of business records go through their policies (ADR-0032); Authorization supplies IRecordAccess.
        services.TryAddScoped<IRecordPolicies, RecordPolicies>();

        // The kernel's record shares (ADR-0038): the data library's own small schema, migrated with the modules'.
        services.AddIronDbContext<DataDbContext>();
        services.TryAddScoped<IRecordSharing, RecordSharing>();
        services.TryAddScoped<IRecordShares, RecordShares>();
        return services;
    }

    /// <summary>
    /// Registers a module's DbContext against the unit's database, in the module's schema, with snake_case names,
    /// its own migrations history table, the audit stamp interceptor, the owning organisation check and the field-level
    /// history of tracked entities.
    /// </summary>
    public static IServiceCollection AddIronDbContext<TContext>(this IServiceCollection services, Action<DbContextOptionsBuilder>? configure = null)
        where TContext : ModuleDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        var schema = SchemaAttribute.For(typeof(TContext));

        services.AddSingleton(new RegisteredContext(typeof(TContext), schema));
        services.AddDbContext<TContext>((provider, options) =>
        {
            DesignTime.Configure<TContext>(options, provider.GetRequiredService<SharedConnection>().Connection);
            options.AddInterceptors(provider.GetRequiredService<OwningOrganisationInterceptor>(), provider.GetRequiredService<AuditStampInterceptor>(), provider.GetRequiredService<ChangeHistoryInterceptor>());
            configure?.Invoke(options);
        });

        return services;
    }
}
