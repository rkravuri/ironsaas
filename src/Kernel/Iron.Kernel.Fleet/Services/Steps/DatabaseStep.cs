using System.Security.Cryptography;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Security.Secrets;
using Npgsql;

namespace Iron.Kernel.Fleet.Services.Steps;

/// <summary>
/// Step 2: the owner role (migrations, seeds), the app role (runtime), the audit verifier's role (ADR-0050, which
/// <c>migrate</c> lets read the chain's tables and nothing else) and the database, as the platform superuser.
/// Passwords are generated once and kept in the store at <c>tenants/&lt;slug&gt;/database</c>; a re-run reuses them.
/// PUBLIC loses CONNECT on the database, so no other tenant's role can reach it (TF-005).
/// An update runs it too (ADR-0028): with the secret in place nothing changes; with the secret lost, new passwords are
/// kept and the roles altered to them, which heals a unit whose store forgot its credentials.
/// </summary>
public sealed class DatabaseStep(ISecretStore secrets) : IProvisioningStep
{
    public const string OwnerPasswordKey = "owner_password";
    public const string AppPasswordKey = "app_password";

    /// <summary>The audit verifier's password (ADR-0050): the fleet reads the chain with it, never the unit.</summary>
    public const string AuditPasswordKey = "audit_password";

    public string Name => StepNames.Database;

    public int Order => 2;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision, JobKind.Update];

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var admin = context.Options.Database.AdminConnectionString ?? throw new InvalidOperationException("Fleet:Database:AdminConnectionString is required to create tenant databases.");

        var existing = await secrets.ReadAsync(context.DatabaseSecretPath, cancellationToken).ConfigureAwait(false);
        var ownerPassword = existing?.GetValueOrDefault(OwnerPasswordKey) ?? NewPassword();
        var appPassword = existing?.GetValueOrDefault(AppPasswordKey) ?? NewPassword();
        var auditPassword = existing?.GetValueOrDefault(AuditPasswordKey) ?? NewPassword();
        if (existing is null || !existing.ContainsKey(AuditPasswordKey))
        {
            // A unit made before the chain gets its verifier's password beside the two it has.
            await secrets.WriteAsync(context.DatabaseSecretPath, new Dictionary<string, string>(StringComparer.Ordinal) { [OwnerPasswordKey] = ownerPassword, [AppPasswordKey] = appPassword, [AuditPasswordKey] = auditPassword }, cancellationToken).ConfigureAwait(false);
        }

        await using var dataSource = NpgsqlDataSource.Create(admin);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureRoleAsync(connection, context.OwnerRole, ownerPassword, cancellationToken).ConfigureAwait(false);
        await EnsureRoleAsync(connection, context.AppRole, appPassword, cancellationToken).ConfigureAwait(false);
        await EnsureRoleAsync(connection, context.AuditReaderRole, auditPassword, cancellationToken).ConfigureAwait(false);

        var database = context.Tenant.DatabaseName;
        var created = false;
        if (!await ExistsAsync(new NpgsqlCommand("select 1 from pg_database where datname = @name", connection), database, cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(connection, $"CREATE DATABASE {Quote(database)} OWNER {Quote(context.OwnerRole)}", cancellationToken).ConfigureAwait(false);
            created = true;
        }

        await ExecuteAsync(connection, $"REVOKE ALL ON DATABASE {Quote(database)} FROM PUBLIC", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"GRANT CONNECT ON DATABASE {Quote(database)} TO {Quote(context.AppRole)}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"GRANT CONNECT ON DATABASE {Quote(database)} TO {Quote(context.AuditReaderRole)}", cancellationToken).ConfigureAwait(false);
        return $"database {database} {(created ? "created" : "exists")}; roles {context.OwnerRole}, {context.AppRole} and {context.AuditReaderRole}";
    }

    private static async Task EnsureRoleAsync(NpgsqlConnection connection, string role, string password, CancellationToken cancellationToken)
    {
        var sql = await ExistsAsync(new NpgsqlCommand("select 1 from pg_roles where rolname = @name", connection), role, cancellationToken).ConfigureAwait(false)
            ? $"ALTER ROLE {Quote(role)} WITH LOGIN PASSWORD {Literal(password)}"
            : $"CREATE ROLE {Quote(role)} WITH LOGIN PASSWORD {Literal(password)}";
        await ExecuteAsync(connection, sql, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ExistsAsync(NpgsqlCommand query, string name, CancellationToken cancellationToken)
    {
        await using (query)
        {
            query.Parameters.AddWithValue("name", name);
            return await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
    }

    [SqlFromIdentifiers("Runs the definitions this step builds: CREATE DATABASE, CREATE or ALTER ROLE and GRANT name the tenant's database and roles, quoted, and a role's generated password as a quoted literal; PostgreSQL takes no parameters there.")]
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string NewPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
}
