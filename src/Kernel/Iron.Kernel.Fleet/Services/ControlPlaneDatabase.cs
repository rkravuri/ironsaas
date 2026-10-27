using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Security.Secrets;
using Npgsql;

namespace Iron.Kernel.Fleet.Services;

/// <summary>
/// The fleet image's command <c>prepare-control</c> (ADR-0053): gives the control plane the split a unit has. As the
/// platform's superuser it makes three roles: <c>control_owner</c>, which owns the database and runs its migrations and
/// seeds; <c>control_app</c>, which the control plane's API and the fleet run with and which <c>migrate</c> grants only
/// what they need; and <c>control_audit</c>, which reads the audit chain and nothing else. A database made before this
/// moves to the owner, with every schema, table, sequence, view and function in it. It makes the container of the
/// control plane's anchors and a SAS that may read, list and create blobs there, never write over or delete one. It then
/// writes the control plane's secret with the app role's connection and that SAS, and the migrate secret with the
/// owner's connection and the two roles to grant. Safe to run again: passwords made once are kept.
/// </summary>
public sealed class ControlPlaneDatabase(FleetOptions options, ISecretStore secrets, TimeProvider time) : IUnitCommand
{
    public const string OwnerPasswordKey = "owner_password";
    public const string AppPasswordKey = "app_password";
    public const string ReaderPasswordKey = "audit_password";

    /// <summary>How long the anchors' SAS lasts; <c>make up-control</c> makes a new one each time it runs.</summary>
    public const int AnchorsSasValidDays = 730;

    // Every object of the control database's own schemas moves to the owner. The names come from the catalogue and are
    // quoted by format's %I; the owner's name is this constant.
    private const string MoveToOwner = """
        DO $$
        DECLARE item record;
        BEGIN
          FOR item IN SELECT nspname FROM pg_namespace
                      WHERE nspname NOT LIKE 'pg\_%' AND nspname NOT IN ('information_schema', 'public')
                        AND pg_get_userbyid(nspowner) <> 'control_owner' LOOP
            EXECUTE format('ALTER SCHEMA %I OWNER TO control_owner', item.nspname);
          END LOOP;
          FOR item IN SELECT n.nspname, c.relname, c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
                        AND c.relkind IN ('r', 'p', 'f', 'v', 'm', 'S')
                        AND pg_get_userbyid(c.relowner) <> 'control_owner'
                        AND NOT (c.relkind = 'S' AND EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype IN ('a', 'i'))) LOOP
            EXECUTE format(
              CASE item.relkind WHEN 'v' THEN 'ALTER VIEW %I.%I OWNER TO control_owner'
                                WHEN 'm' THEN 'ALTER MATERIALIZED VIEW %I.%I OWNER TO control_owner'
                                WHEN 'S' THEN 'ALTER SEQUENCE %I.%I OWNER TO control_owner'
                                ELSE 'ALTER TABLE %I.%I OWNER TO control_owner' END,
              item.nspname, item.relname);
          END LOOP;
          FOR item IN SELECT n.nspname, p.proname, pg_get_function_identity_arguments(p.oid) AS arguments
                      FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                      WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
                        AND pg_get_userbyid(p.proowner) <> 'control_owner' LOOP
            EXECUTE format('ALTER ROUTINE %I.%I(%s) OWNER TO control_owner', item.nspname, item.proname, item.arguments);
          END LOOP;
        END $$;
        """;

    public string Name => DatabaseCommands.PrepareControl;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var admin = options.Database.AdminConnectionString ?? throw new InvalidOperationException("Fleet:Database:AdminConnectionString is required to prepare the control plane's database.");
        var account = options.Storage.AccountConnectionString ?? throw new InvalidOperationException("Fleet:Storage:AccountConnectionString is required to make the control plane's anchors container.");
        var unit = await secrets.ReadAsync(ControlPlaneNames.UnitSecretPath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The control plane has no secret yet: make secret-control writes it first.");

        var passwords = await PasswordsAsync(cancellationToken).ConfigureAwait(false);
        await PrepareDatabaseAsync(admin, passwords, cancellationToken).ConfigureAwait(false);
        var anchors = await PrepareAnchorsAsync(account, cancellationToken).ConfigureAwait(false);

        var runtime = new Dictionary<string, string>(unit, StringComparer.Ordinal)
        {
            ["ConnectionStrings:Database"] = ConnectionString(admin, ControlPlaneNames.AppRole, passwords[AppPasswordKey]),
            ["ControlAudit:AnchorsSasUrl"] = anchors,
        };
        var migrate = new Dictionary<string, string>(runtime, StringComparer.Ordinal)
        {
            ["ConnectionStrings:Database"] = ConnectionString(admin, ControlPlaneNames.OwnerRole, passwords[OwnerPasswordKey]),
            ["Database:AppRole"] = ControlPlaneNames.AppRole,
            ["Database:AuditReaderRole"] = ControlPlaneNames.ReaderRole,
        };
        await secrets.WriteAsync(ControlPlaneNames.UnitSecretPath, runtime, cancellationToken).ConfigureAwait(false);
        await secrets.WriteAsync(ControlPlaneNames.MigrateSecretPath, migrate, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Prepared the control plane: database {ControlPlaneNames.Database} owned by {ControlPlaneNames.OwnerRole}, the API and the fleet as {ControlPlaneNames.AppRole}, verification as {ControlPlaneNames.ReaderRole}, anchors in {ControlPlaneNames.AnchorsContainer}. Migrate and seed with Secrets__Path=control-migrate.").ConfigureAwait(false);
        return 0;
    }

    /// <summary>The control plane's database as a role reaches it, on the same route as the fleet's own connection.</summary>
    public static string ConnectionString(string admin, string role, string password) =>
        new NpgsqlConnectionStringBuilder(admin) { Database = ControlPlaneNames.Database, Username = role, Password = password }.ConnectionString;

    private async Task<Dictionary<string, string>> PasswordsAsync(CancellationToken cancellationToken)
    {
        var existing = await secrets.ReadAsync(ControlPlaneNames.DatabaseSecretPath, cancellationToken).ConfigureAwait(false);
        var passwords = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OwnerPasswordKey] = existing?.GetValueOrDefault(OwnerPasswordKey) ?? NewPassword(),
            [AppPasswordKey] = existing?.GetValueOrDefault(AppPasswordKey) ?? NewPassword(),
            [ReaderPasswordKey] = existing?.GetValueOrDefault(ReaderPasswordKey) ?? NewPassword(),
        };
        if (existing is null || passwords.Any(p => existing.GetValueOrDefault(p.Key) != p.Value))
        {
            await secrets.WriteAsync(ControlPlaneNames.DatabaseSecretPath, passwords, cancellationToken).ConfigureAwait(false);
        }

        return passwords;
    }

    private static async Task PrepareDatabaseAsync(string admin, Dictionary<string, string> passwords, CancellationToken cancellationToken)
    {
        await using (var server = NpgsqlDataSource.Create(admin))
        {
            await EnsureRoleAsync(server, ControlPlaneNames.OwnerRole, passwords[OwnerPasswordKey], cancellationToken).ConfigureAwait(false);
            await EnsureRoleAsync(server, ControlPlaneNames.AppRole, passwords[AppPasswordKey], cancellationToken).ConfigureAwait(false);
            await EnsureRoleAsync(server, ControlPlaneNames.ReaderRole, passwords[ReaderPasswordKey], cancellationToken).ConfigureAwait(false);
            await using var exists = server.CreateCommand("select 1 from pg_database where datname = 'control'");
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                await ExecuteAsync(server, "CREATE DATABASE control OWNER control_owner", cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(server, "ALTER DATABASE control OWNER TO control_owner", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(server, "REVOKE ALL ON DATABASE control FROM PUBLIC", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(server, "GRANT CONNECT ON DATABASE control TO control_app, control_audit", cancellationToken).ConfigureAwait(false);
        }

        // A database the superuser made and migrated before the split: what is in it moves to the owner.
        await using var database = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(admin) { Database = ControlPlaneNames.Database }.ConnectionString);
        await ExecuteAsync(database, MoveToOwner, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> PrepareAnchorsAsync(string account, CancellationToken cancellationToken)
    {
        var container = new BlobContainerClient(account, ControlPlaneNames.AnchorsContainer);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!container.CanGenerateSasUri)
        {
            throw new InvalidOperationException("Fleet:Storage:AccountConnectionString needs the account key to sign the anchors' SAS.");
        }

        // Read, list and create: a new anchor can be added, an existing one never written over or deleted.
        var permissions = BlobContainerSasPermissions.Read | BlobContainerSasPermissions.List | BlobContainerSasPermissions.Create;
        return container.GenerateSasUri(permissions, time.GetUtcNow().AddDays(AnchorsSasValidDays)).ToString();
    }

    [SqlFromIdentifiers("CREATE or ALTER ROLE names one of the three constant roles above and sets a password this command generated, as a quoted literal; PostgreSQL takes no parameters there.")]
    private static async Task EnsureRoleAsync(NpgsqlDataSource server, string role, string password, CancellationToken cancellationToken)
    {
        await using var exists = server.CreateCommand("select 1 from pg_roles where rolname = @role");
        exists.Parameters.AddWithValue("role", role);
        var verb = await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null ? "CREATE" : "ALTER";
        await using var command = server.CreateCommand($"{verb} ROLE \"{role}\" WITH LOGIN PASSWORD '{password.Replace("'", "''", StringComparison.Ordinal)}'");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [SqlFromIdentifiers("Runs this command's constant definitions, which name only the database control and the three constant roles; the analyzer cannot follow a constant through the helper's parameter.")]
    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NewPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
}
