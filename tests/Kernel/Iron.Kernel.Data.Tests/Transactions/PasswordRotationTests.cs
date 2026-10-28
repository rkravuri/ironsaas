using System.Security.Cryptography;
using Iron.Kernel.Data.Tests.Sample;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Kernel.Data.Tests.Transactions;

/// <summary>
/// A database password rotated while the unit serves (TF-073 AC2, ADR-0054): the store holds the new password first, the
/// database takes it next, and the unit's new connections use it without a restart while every request goes on.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PasswordRotationTests(PostgresFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-073 AC2", "ADR-0054")]
    public async Task ARotatedPasswordIsUsedByNewConnectionsWhileEveryRequestSucceeds()
    {
        var role = $"rotation_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        const string First = "first-password-of-the-role";
        const string Second = "second-password-of-the-role";
        await AdminAsync($"CREATE ROLE {role} LOGIN PASSWORD '{First}'");
        await AdminAsync($"GRANT USAGE ON SCHEMA kernel_sample TO {role}");
        await AdminAsync($"GRANT SELECT, INSERT ON kernel_sample.preferences TO {role}");
        var store = new StoreStandIn(First);
        await using var provider = Build(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Username = role, Password = First, MaxPoolSize = 4 }.ConnectionString, store);
        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
        var failures = new List<Exception>();
        var requests = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);

        async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var scope = provider.CreateAsyncScope();
                    var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
                    await using var transaction = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(Cancel);
                    context.Preferences.Add(new Preference($"rotation-{Ids.New():N}", "kept"));
                    await context.SaveChangesAsync(Cancel);
                    await transaction.CommitAsync(Cancel);
                    await context.Preferences.AsNoTracking().CountAsync(Cancel);
                    Interlocked.Increment(ref requests);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lock (failures)
                    {
                        failures.Add(exception);
                    }
                }
            }
        }

        var serving = Enumerable.Range(0, 4).Select(_ => Task.Run(ServeAsync, Cancel)).ToList();
        await Task.Delay(TimeSpan.FromSeconds(1), Cancel);
        var beforeRotation = requests;

        // The fleet's order: the store first, then the database; then every idle connection goes, so the next ones are new.
        store.Password = Second;
        await AdminAsync($"ALTER ROLE {role} PASSWORD '{Second}'");
        dataSource.Clear();
        await Task.Delay(TimeSpan.FromSeconds(2), Cancel);
        await stop.CancelAsync();
        await Task.WhenAll(serving);

        Assert.Empty(failures);
        Assert.True(beforeRotation > 0 && requests > beforeRotation, $"{beforeRotation} requests before the rotation, {requests} in all.");
        Assert.True(store.Invalidations > 0, "A connection refused the old password read the store again.");
        await using var old = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Username = role, Password = First, Pooling = false }.ConnectionString);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => old.OpenAsync(Cancel));
        Assert.True(DatabaseConnections.IsRefusedPassword(refused));
    }

    private ServiceProvider Build(string connectionString, IDatabaseCredentials credentials)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
        services.AddSingleton<ICurrentPrincipal>(new FixedPrincipal(Ids.New()));
        services.AddSingleton(credentials);
        services.AddIronDatabase(connectionString);
        services.AddIronDbContext<SampleDbContext>(options => options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        return services.BuildServiceProvider();
    }

    private async Task AdminAsync(string sql)
    {
        await using var command = fixture.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    /// <summary>
    /// The store as the unit reads it: the password is read once and kept, as within the refresh interval, until a refusal
    /// asks for it again.
    /// </summary>
    private sealed class StoreStandIn(string password) : IDatabaseCredentials
    {
        private string? _kept;
        private int _invalidations;

        public string Password { get; set; } = password;

        public int Invalidations => _invalidations;

        public bool Rotates => true;

        public ValueTask<string?> PasswordAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(_kept ??= Password);

        public void Invalidate()
        {
            Interlocked.Increment(ref _invalidations);
            _kept = null;
        }
    }
}
