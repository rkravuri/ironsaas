using Iron.Kernel.Data.Commands;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// The old column of authenticator secrets, sealed under the factor key of ADR-0047, goes in V0 (ADR-0054): a unit whose
/// secrets were never moved onto the key service refuses the migration, losing nothing, and a unit whose secrets were
/// moved migrates and keeps every authenticator app.
/// </summary>
[Collection(IdentityCollection.Name)]
public class RetiredFactorColumnTests(IdentityFixture fixture)
{
    /// <summary>T9b's migration, the last that keeps the old column.</summary>
    private const string BeforeTheDrop = "20261028013724_SecretsThroughTheKeyService";

    private const string Dropped = "20261029112821_RetiredFactorSecretColumnDropped";

    private static readonly DateTimeOffset Now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("ADR-0054", "ADR-0047")]
    public async Task AUnitWhoseSecretsWereNeverMovedRefusesTheDropAndOneWhoseSecretsMovedKeepsThem()
    {
        var database = await fixture.CreateTenantDatabaseAsync($"retired_{Ids.New():N}");
        await using var provider = fixture.BuildServices(new FakeTimeProvider(Now), connectionString: database);
        var secret = Totp.NewSecret();
        Guid factorId;
        Guid personId;
        string moved;
        await using (var scope = provider.CreateAsyncScope())
        {
            // The database as T9b left it, with one authenticator app whose secret the update after T9b never moved.
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await context.GetService<IMigrator>().MigrateAsync(BeforeTheDrop, Cancel);
            var person = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, $"retired-{Ids.New():N}@acme.test", "correct horse battery staple", "Rita Retired");
            var sealedSecret = await scope.ServiceProvider.GetRequiredService<FactorSecrets>().SealAsync(secret, person.Id, Cancel);
            var factor = Factor.AuthenticatorApp(person.Id, "Phone", sealedSecret, 1, Now);
            context.Factors.Add(factor);
            await context.SaveChangesAsync(Cancel);
            (factorId, personId, moved) = (factor.Id, person.Id, sealedSecret.Ciphertext);
            var old = new byte[45];
            old[0] = 1;
            await context.Database.ExecuteSqlAsync($"update kernel_identity.factors set secret_ciphertext = {old}, secret = null where id = {factorId}", Cancel);
        }

        // The migrate command the fleet's update runs exits with a failure, and so the update stops before the unit starts.
        Assert.Equal(1, await DatabaseCommands.RunAsync(provider, [DatabaseCommands.Migrate], Cancel));
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var refused = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync(Cancel));
            Assert.Equal(PostgresErrorCodes.RaiseException, refused.SqlState);
            Assert.False(refused.IsTransient, "A refusal that retrying cannot change.");
            Assert.StartsWith("1 authenticator app secrets are still sealed under the retired factor key.", refused.MessageText, StringComparison.Ordinal);
            Assert.DoesNotContain(Dropped, await context.Database.GetAppliedMigrationsAsync(Cancel));
        }

        Assert.True(await OldColumnExistsAsync(database));
        Assert.Equal(1L, await ScalarAsync(database, $"select count(*) from kernel_identity.factors where secret_ciphertext is not null and id = '{factorId}'"));

        // The update after T9b moves it: the secret is on the key service and the old column is empty. Now the drop runs.
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await context.Database.ExecuteSqlAsync($"update kernel_identity.factors set secret = {moved}, secret_ciphertext = null where id = {factorId}", Cancel);
        }

        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, [DatabaseCommands.Migrate], Cancel));
        Assert.False(await OldColumnExistsAsync(database));
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            Assert.Contains(Dropped, await context.Database.GetAppliedMigrationsAsync(Cancel));
            var app = await context.Factors.AsNoTracking().SingleAsync(f => f.Id == factorId, Cancel);
            Assert.Equal(secret, await scope.ServiceProvider.GetRequiredService<FactorSecrets>().OpenAsync(app.Secret!, personId, Cancel));
        }
    }

    private static async Task<bool> OldColumnExistsAsync(string database) =>
        await ScalarAsync(database, "select count(*) from pg_attribute where attrelid = 'kernel_identity.factors'::regclass and attname = 'secret_ciphertext' and not attisdropped") == 1L;

    private static async Task<long> ScalarAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }
}
