using System.Globalization;
using System.Security.Cryptography;
using Iron.Kernel.Fleet.Services.Steps;
using Iron.Kernel.Security.Keys;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iron.Kernel.Fleet.Services;

/// <summary>
/// One pass of the fleet's key work (TF-073, TF-075, ADR-0054): ask for the rotations the schedule says are due, carry
/// out each rotation asked for, in the key service or, for a database password, in the store and then the database, and
/// retire the old version of every key whose values have all moved, or whose tokens have all expired. A rotation that
/// fails is recorded with its error and raises the alert; the key keeps working on its old version.
/// </summary>
public sealed class KeyRotationRunner(ITenantKeys keys, IKeyAdministration administration, DatabasePasswordRotation passwords, ILogger<KeyRotationRunner> logger)
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var worked = await keys.ScheduleDueAsync(cancellationToken).ConfigureAwait(false);
        foreach (var rotation in await keys.RequestedAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (TenantKeyNames.PurposeOf(rotation.Key) is { } purpose)
                {
                    var rotated = await administration.RotateAsync(KeyPurposes.KeyName(KeyPurposes.TenantPrefix(rotation.TenantSlug), purpose), cancellationToken).ConfigureAwait(false);
                    await keys.RotatedAsync(rotation.Id, rotated.Latest - 1, rotated.Latest, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var (from, to) = await passwords.RotateAsync(rotation.TenantSlug, cancellationToken).ConfigureAwait(false);
                    await keys.RotatedAsync(rotation.Id, from, to, cancellationToken).ConfigureAwait(false);
                }

                worked++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The rotation of the {Key} key of tenant {Slug} failed.", rotation.Key, rotation.TenantSlug);
                await keys.FailedAsync(rotation.Id, exception.Message, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var rotation in await keys.RetirableAsync(cancellationToken).ConfigureAwait(false))
        {
            if (TenantKeyNames.PurposeOf(rotation.Key) is not { } purpose || rotation.ToVersion is not { } version)
            {
                continue;
            }

            var retired = await administration.RetireAsync(KeyPurposes.KeyName(KeyPurposes.TenantPrefix(rotation.TenantSlug), purpose), version, cancellationToken).ConfigureAwait(false);
            await keys.RetiredAsync(rotation.Id, retired.Minimum, cancellationToken).ConfigureAwait(false);
            worked++;
        }

        return worked;
    }
}

/// <summary>The fleet host's loop for keys: a pass every few seconds, so an operator's rotation is carried out at once.</summary>
public sealed class KeyRotationWorker(IServiceScopeFactory scopes, FleetOptions options, TimeProvider time, ILogger<KeyRotationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Keys.PollSeconds)), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<KeyRotationRunner>().RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The key worker's pass failed; it tries again at the next.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Rotates a tenant's database password (TF-073 AC2, ADR-0054): the new password goes into the store first, in the
/// database secret and in the unit's connection string, and only then into the database. The unit reads the store again
/// when the database refuses its old password, so its new connections use the new one without a restart, and its open
/// connections go on. The count of passwords the role has had is kept beside it, as the password's version.
/// </summary>
public sealed class DatabasePasswordRotation(ITenantRegistry tenants, ISecretStore secrets, FleetOptions options)
{
    public const string VersionKey = "app_password_version";

    public async Task<(int From, int To)> RotateAsync(string slug, CancellationToken cancellationToken)
    {
        var tenant = await tenants.FindAsync(slug, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"There is no tenant '{slug}'.");
        var context = new ProvisioningContext(tenant, options);
        var admin = options.Database.AdminConnectionString ?? throw new InvalidOperationException("Fleet:Database:AdminConnectionString is required to rotate a database password.");
        var database = await secrets.ReadAsync(context.DatabaseSecretPath, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The tenant has no database secret: provision or update it first.");
        var unit = await secrets.ReadAsync(context.UnitSecretPath, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The tenant has no unit secret: provision or update it first.");
        var from = int.TryParse(database.GetValueOrDefault(VersionKey), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : 1;
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

        await secrets.WriteAsync(context.DatabaseSecretPath, new Dictionary<string, string>(database, StringComparer.Ordinal)
        {
            [DatabaseStep.AppPasswordKey] = password,
            [VersionKey] = (from + 1).ToString(CultureInfo.InvariantCulture),
        }, cancellationToken).ConfigureAwait(false);
        await secrets.WriteAsync(context.UnitSecretPath, new Dictionary<string, string>(unit, StringComparer.Ordinal)
        {
            [StoreDatabaseCredentials.ConnectionStringKey] = context.ConnectionString(context.AppRole, password),
        }, cancellationToken).ConfigureAwait(false);

        await using var dataSource = NpgsqlDataSource.Create(admin);
        await SetPasswordAsync(dataSource, context.AppRole, password, cancellationToken).ConfigureAwait(false);
        return (from, from + 1);
    }

    [SqlFromIdentifiers("ALTER ROLE names the tenant's app role, quoted, and sets a password this rotation generated, as a quoted literal; PostgreSQL takes no parameters there.")]
    private static async Task SetPasswordAsync(NpgsqlDataSource dataSource, string role, string password, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"ALTER ROLE \"{role.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WITH PASSWORD '{password.Replace("'", "''", StringComparison.Ordinal)}'");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
