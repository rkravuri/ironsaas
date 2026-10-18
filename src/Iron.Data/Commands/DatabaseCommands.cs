using Iron.Data.Checks;
using Iron.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iron.Data.Commands;

/// <summary>
/// The commands a host image answers besides serving: <c>migrate</c> applies every module's migrations and grants the
/// app role its privileges, <c>seed</c> runs every seeder, <c>schema-check</c> checks the unit's schemas against TF-080,
/// <c>grant [role]</c> grants the privileges again. The fleet runs them against a new tenant database (T14); the
/// pipeline runs <c>schema-check</c> after migrating (T18).
/// </summary>
public static class DatabaseCommands
{
    public const string Migrate = "migrate";
    public const string Seed = "seed";
    public const string SchemaCheck = "schema-check";
    public const string Grant = "grant";

    public static bool IsCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && args[0] is Migrate or Seed or SchemaCheck or Grant;
    }

    /// <summary>The arguments without the command, for the web host builder.</summary>
    public static string[] StripCommand(string[] args) => IsCommand(args) ? args[1..] : args;

    /// <summary>Runs the command and returns the process exit code: 0 on success, 1 when the schema check finds violations.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var options = provider.GetRequiredService<DatabaseOptions>();
        var logger = provider.GetRequiredService<ILogger<GrantsRunner>>();
        switch (args[0])
        {
            case Migrate:
                await provider.GetRequiredService<MigrationRunner>().RunAsync(cancellationToken).ConfigureAwait(false);
                if (options.AppRole is not null)
                {
                    await provider.GetRequiredService<GrantsRunner>().RunAsync(options.AppRole, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    logger.LogWarning("No app role is configured (Database:AppRole); the runtime privileges were not granted.");
                }

                return 0;
            case Grant:
                var role = args.Length > 1 ? args[1] : options.AppRole;
                if (role is null)
                {
                    logger.LogError("The grant command needs a role: 'grant <role>' or Database:AppRole.");
                    return 1;
                }

                await provider.GetRequiredService<GrantsRunner>().RunAsync(role, cancellationToken).ConfigureAwait(false);
                return 0;
            case Seed:
                await provider.GetRequiredService<SeedRunner>().RunAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            case SchemaCheck:
                var violations = await provider.GetRequiredService<SchemaCheckRunner>().RunAsync(cancellationToken).ConfigureAwait(false);
                return violations.Count == 0 ? 0 : 1;
            default:
                throw new ArgumentException($"'{args[0]}' is not a command. Use {Migrate}, {Seed}, {SchemaCheck} or {Grant}.", nameof(args));
        }
    }
}

/// <summary>Applies the pending migrations of every registered module context, in registration order.</summary>
public sealed class MigrationRunner(IServiceProvider provider, IEnumerable<RegisteredContext> contexts, ILogger<MigrationRunner> logger)
{
    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken cancellationToken)
    {
        var applied = new List<string>();
        var registered = contexts.ToList();
        if (registered.Count == 0)
        {
            logger.LogWarning("No module contexts are registered; nothing to migrate.");
            return applied;
        }

        foreach (var registration in registered)
        {
            var context = (DbContext)provider.GetRequiredService(registration.ContextType);
            var history = context.GetService<IHistoryRepository>();
            var pending = await history.ExistsAsync(cancellationToken).ConfigureAwait(false)
                ? (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList()
                : context.Database.GetMigrations().ToList(); // a new schema: nothing to compare against yet
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            applied.AddRange(pending.Select(migration => $"{registration.Schema}: {migration}"));
            logger.LogInformation("Schema {Schema}: {Count} migration(s) applied by {Context}.", registration.Schema, pending.Count, registration.ContextType.Name);
        }

        return applied;
    }
}

/// <summary>Runs every registered seeder, lowest order first.</summary>
public sealed class SeedRunner(IEnumerable<ISeeder> seeders, ILogger<SeedRunner> logger)
{
    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken cancellationToken)
    {
        var ran = new List<string>();
        foreach (var seeder in seeders.OrderBy(s => s.Order).ThenBy(s => s.GetType().Name, StringComparer.Ordinal))
        {
            await seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
            ran.Add(seeder.GetType().Name);
            logger.LogInformation("Seeder {Seeder} (order {Order}) done.", seeder.GetType().Name, seeder.Order);
        }

        if (ran.Count == 0)
        {
            logger.LogWarning("No seeders are registered; nothing to seed.");
        }

        return ran;
    }
}

/// <summary>Checks the schemas of every registered module context against the table conventions (TF-080).</summary>
public sealed class SchemaCheckRunner(NpgsqlDataSource dataSource, IEnumerable<RegisteredContext> contexts, ILogger<SchemaCheckRunner> logger)
{
    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken cancellationToken)
    {
        var schemas = contexts.Select(c => c.Schema).Distinct(StringComparer.Ordinal).ToList();
        if (schemas.Count == 0)
        {
            logger.LogWarning("No module contexts are registered; nothing to check.");
            return [];
        }

        var violations = await Checks.SchemaCheck.RunAsync(dataSource, schemas, cancellationToken).ConfigureAwait(false);
        foreach (var violation in violations)
        {
            logger.LogError("Schema violation: {Violation}", violation);
        }

        logger.LogInformation("Schema check over {Schemas}: {Count} violation(s).", string.Join(", ", schemas), violations.Count);
        return violations;
    }
}
