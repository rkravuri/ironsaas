using System.Text.RegularExpressions;
using Iron.Kernel.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iron.Kernel.Data.Commands;

/// <summary>
/// The privileges of the runtime database role (the app role): usage on every module schema, SELECT, INSERT, UPDATE and
/// DELETE on ordinary tables, and SELECT and INSERT only on append-only tables, so no code path can change or delete an
/// audit row (TF-060 AC2). It may run the functions a module's migration defines, such as the one that seals an audit
/// event's chain columns once (ADR-0050). The audit verifier's role reads the tables marked <see cref="IVerifierReadable"/>
/// and nothing else (TF-061, ADR-0050). Runs as the owner role after <c>migrate</c>; safe to run again.
/// </summary>
public sealed partial class GrantsRunner(NpgsqlDataSource dataSource, IEnumerable<RegisteredContext> contexts, IServiceProvider provider, ILogger<GrantsRunner> logger)
{
    public static bool IsValidRole(string? role) => role is not null && RolePattern().IsMatch(role);

    public async Task<IReadOnlyList<string>> RunAsync(string role, CancellationToken cancellationToken)
    {
        if (!IsValidRole(role))
        {
            throw new ArgumentException($"'{role}' is not a database role name. Use lowercase letters, digits and underscores.", nameof(role));
        }

        var models = contexts
            .Select(registration => (registration.Schema, ((DbContext)provider.GetRequiredService(registration.ContextType)).Model))
            .ToList();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var statements = Statements(connection.Database, role, models);
        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("Granted the app role {Role} its privileges over {Schemas} ({Count} statements).", role, string.Join(", ", models.Select(m => m.Schema)), statements.Count);
        return statements;
    }

    /// <summary>The audit verifier's role (ADR-0050): connect, and read the chain's tables, nothing else.</summary>
    public async Task<IReadOnlyList<string>> RunReaderAsync(string role, CancellationToken cancellationToken)
    {
        if (!IsValidRole(role))
        {
            throw new ArgumentException($"'{role}' is not a database role name. Use lowercase letters, digits and underscores.", nameof(role));
        }

        var models = contexts
            .Select(registration => (registration.Schema, ((DbContext)provider.GetRequiredService(registration.ContextType)).Model))
            .ToList();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var statements = ReaderStatements(connection.Database, role, models);
        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("Granted the audit verifier's role {Role} its reads ({Count} statements).", role, statements.Count);
        return statements;
    }

    /// <summary>The pure part of the verifier's grants: the tables marked <see cref="IVerifierReadable"/>, read only.</summary>
    public static IReadOnlyList<string> ReaderStatements(string database, string role, IReadOnlyList<(string Schema, IModel Model)> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var statements = new List<string> { $"GRANT CONNECT ON DATABASE {Quote(database)} TO {Quote(role)}" };
        foreach (var (schema, model) in models)
        {
            var readable = model.GetEntityTypes()
                .Where(entityType => typeof(IVerifierReadable).IsAssignableFrom(entityType.ClrType) && entityType.GetTableName() is not null)
                .ToList();
            if (readable.Count == 0)
            {
                continue;
            }

            statements.Add($"GRANT USAGE ON SCHEMA {Quote(schema)} TO {Quote(role)}");
            statements.Add($"REVOKE ALL ON ALL TABLES IN SCHEMA {Quote(schema)} FROM {Quote(role)}");
            statements.AddRange(readable.Select(entityType => $"GRANT SELECT ON {Quote(entityType.GetSchema() ?? schema)}.{Quote(entityType.GetTableName()!)} TO {Quote(role)}"));
        }

        return statements;
    }

    /// <summary>The pure part: the statements for a role over the given schemas and models.</summary>
    public static IReadOnlyList<string> Statements(string database, string role, IReadOnlyList<(string Schema, IModel Model)> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var statements = new List<string> { $"GRANT CONNECT ON DATABASE {Quote(database)} TO {Quote(role)}" };
        foreach (var (schema, model) in models)
        {
            statements.Add($"GRANT USAGE ON SCHEMA {Quote(schema)} TO {Quote(role)}");
            statements.Add($"REVOKE ALL ON ALL TABLES IN SCHEMA {Quote(schema)} FROM {Quote(role)}");

            // A function a migration defines is there for the app; each revokes PUBLIC's execute itself (ADR-0050).
            statements.Add($"GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA {Quote(schema)} TO {Quote(role)}");
            foreach (var entityType in model.GetEntityTypes())
            {
                var table = entityType.GetTableName();
                if (table is null)
                {
                    continue;
                }

                var privileges = typeof(AppendOnlyEntity).IsAssignableFrom(entityType.ClrType) ? "SELECT, INSERT" : "SELECT, INSERT, UPDATE, DELETE";
                statements.Add($"GRANT {privileges} ON {Quote(entityType.GetSchema() ?? schema)}.{Quote(table)} TO {Quote(role)}");
            }
        }

        return statements;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
