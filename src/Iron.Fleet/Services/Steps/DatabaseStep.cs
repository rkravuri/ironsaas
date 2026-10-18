using System.Security.Cryptography;
using Iron.Fleet.Contracts;
using Iron.Security.Secrets;
using Npgsql;

namespace Iron.Fleet.Services.Steps;

/// <summary>
/// Step 2: the owner role (migrations, seeds), the app role (runtime) and the database, as the platform superuser.
/// Passwords are generated once and kept in the store at <c>tenants/&lt;slug&gt;/database</c>; a re-run reuses them.
/// PUBLIC loses CONNECT on the database, so no other tenant's role can reach it (TF-005).
/// </summary>
public sealed class DatabaseStep(ISecretStore secrets) : IProvisioningStep
{
    public const string OwnerPasswordKey = "owner_password";
    public const string AppPasswordKey = "app_password";

    public string Name => StepNames.Database;

    public int Order => 2;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision];

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var admin = context.Options.Database.AdminConnectionString ?? throw new InvalidOperationException("Fleet:Database:AdminConnectionString is required to create tenant databases.");

        var existing = await secrets.ReadAsync(context.DatabaseSecretPath, cancellationToken).ConfigureAwait(false);
        var ownerPassword = existing?.GetValueOrDefault(OwnerPasswordKey) ?? NewPassword();
        var appPassword = existing?.GetValueOrDefault(AppPasswordKey) ?? NewPassword();
        if (existing is null)
        {
            await secrets.WriteAsync(context.DatabaseSecretPath, new Dictionary<string, string>(StringComparer.Ordinal) { [OwnerPasswordKey] = ownerPassword, [AppPasswordKey] = appPassword }, cancellationToken).ConfigureAwait(false);
        }

        await using var dataSource = NpgsqlDataSource.Create(admin);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureRoleAsync(connection, context.OwnerRole, ownerPassword, cancellationToken).ConfigureAwait(false);
        await EnsureRoleAsync(connection, context.AppRole, appPassword, cancellationToken).ConfigureAwait(false);

        var database = context.Tenant.DatabaseName;
        var created = false;
        if (!await ExistsAsync(connection, "select 1 from pg_database where datname = @name", database, cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(connection, $"CREATE DATABASE {Quote(database)} OWNER {Quote(context.OwnerRole)}", cancellationToken).ConfigureAwait(false);
            created = true;
        }

        await ExecuteAsync(connection, $"REVOKE ALL ON DATABASE {Quote(database)} FROM PUBLIC", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"GRANT CONNECT ON DATABASE {Quote(database)} TO {Quote(context.AppRole)}", cancellationToken).ConfigureAwait(false);
        return $"database {database} {(created ? "created" : "exists")}; roles {context.OwnerRole} and {context.AppRole}";
    }

    private static async Task EnsureRoleAsync(NpgsqlConnection connection, string role, string password, CancellationToken cancellationToken)
    {
        var sql = await ExistsAsync(connection, "select 1 from pg_roles where rolname = @name", role, cancellationToken).ConfigureAwait(false)
            ? $"ALTER ROLE {Quote(role)} WITH LOGIN PASSWORD {Literal(password)}"
            : $"CREATE ROLE {Quote(role)} WITH LOGIN PASSWORD {Literal(password)}";
        await ExecuteAsync(connection, sql, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, string sql, string name, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string NewPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
}
