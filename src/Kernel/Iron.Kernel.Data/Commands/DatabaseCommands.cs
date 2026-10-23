using Iron.Kernel.Data.Checks;
using Iron.Kernel.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iron.Kernel.Data.Commands;

/// <summary>
/// The commands a host image answers besides serving: <c>migrate</c> applies every module's migrations and grants the
/// app role its privileges, <c>seed</c> runs every seeder, <c>schema-check</c> checks the unit's schemas against TF-080,
/// <c>grant [role]</c> grants the privileges again. The fleet runs them against a new tenant database (T14); the
/// pipeline runs <c>schema-check</c> after migrating (T18). A module adds a command of its own as an
/// <see cref="IUnitCommand"/>, named here so the host knows it before it is built: <c>recover-admin</c>, the operator's
/// recovery link for a tenant administrator (ADR-0046); <c>impersonate</c>, <c>break-glass-open</c> and
/// <c>break-glass-close</c>, the operator's support and emergency access (ADR-0049).
/// </summary>
public static class DatabaseCommands
{
    public const string Migrate = "migrate";
    public const string Seed = "seed";
    public const string SchemaCheck = "schema-check";
    public const string Grant = "grant";
    public const string RecoverAdministrator = "recover-admin";
    public const string Impersonate = "impersonate";
    public const string BreakGlassOpen = "break-glass-open";
    public const string BreakGlassClose = "break-glass-close";

    public static bool IsCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && args[0] is Migrate or Seed or SchemaCheck or Grant or RecoverAdministrator or Impersonate or BreakGlassOpen or BreakGlassClose;
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
        try
        {
            return await RunCommandAsync(args, provider, options, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ArgumentException))
        {
            logger.LogError(exception, "The {Command} command failed: {Message}", args[0], exception.Message);
            return 1;
        }
    }

    private static async Task<int> RunCommandAsync(string[] args, IServiceProvider provider, DatabaseOptions options, ILogger logger, CancellationToken cancellationToken)
    {
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

                if (options.AuditReaderRole is not null)
                {
                    await provider.GetRequiredService<GrantsRunner>().RunReaderAsync(options.AuditReaderRole, cancellationToken).ConfigureAwait(false);
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
                var command = provider.GetServices<IUnitCommand>().FirstOrDefault(c => string.Equals(c.Name, args[0], StringComparison.Ordinal))
                    ?? throw new ArgumentException($"'{args[0]}' is not a command of this host. Use {Migrate}, {Seed}, {SchemaCheck} or {Grant}.", nameof(args));
                return await command.RunAsync(args[1..], cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A command a module adds to the image (ADR-0046): the host runs it in a scope of its own, like the database commands, and
/// its exit code is the process's. Its name must also be one of <see cref="DatabaseCommands"/>' names.
/// </summary>
public interface IUnitCommand
{
    string Name { get; }

    Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken);
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

        // A suite module's schema starts with its suite's short name, which its assembly carries (ADR-0043).
        var suites = contexts.Select(c => SuiteName.Of(c.ContextType.Assembly)).Where(s => s != SuiteName.Foundation).Distinct(StringComparer.Ordinal).ToList();
        var violations = await Checks.SchemaCheck.RunAsync(dataSource, schemas, suites, cancellationToken).ConfigureAwait(false);
        foreach (var violation in violations)
        {
            logger.LogError("Schema violation: {Violation}", violation);
        }

        logger.LogInformation("Schema check over {Schemas}: {Count} violation(s).", string.Join(", ", schemas), violations.Count);
        return violations;
    }
}
