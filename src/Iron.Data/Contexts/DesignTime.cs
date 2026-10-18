using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Iron.Data.Contexts;

/// <summary>
/// The provider options every module context gets: Npgsql, snake_case names, the migrations history table in the
/// module's schema. AddIronDbContext uses them at run time; a module's design-time factory uses them for
/// <c>dotnet ef migrations add</c>, so generated migrations match what runs.
/// </summary>
public static class DesignTime
{
    public const string ConnectionString = "Host=localhost;Database=design_time;Username=design;Password=design";

    public static void Configure<TContext>(DbContextOptionsBuilder options, NpgsqlDataSource dataSource)
        where TContext : ModuleDbContext
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = SchemaAttribute.For(typeof(TContext));
        options.UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(ModuleDbContext.MigrationsHistoryTable, schema));
        options.UseSnakeCaseNamingConvention();
    }

    public static DbContextOptions<TContext> OptionsFor<TContext>()
        where TContext : ModuleDbContext
    {
        var schema = SchemaAttribute.For(typeof(TContext));
        var options = new DbContextOptionsBuilder<TContext>();
        options.UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable(ModuleDbContext.MigrationsHistoryTable, schema));
        options.UseSnakeCaseNamingConvention();
        return options.Options;
    }
}
