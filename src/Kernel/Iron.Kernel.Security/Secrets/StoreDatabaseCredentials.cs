using System.Data.Common;

namespace Iron.Kernel.Security.Secrets;

/// <summary>
/// The unit's database password from its secret in the store (TF-073 AC2, ADR-0054): read again when it is older than
/// the refresh interval or after a connection was refused it, so a password the fleet rotated reaches new connections
/// within the interval, or at once when the old one stops working. It applies only while the unit secret's login is the
/// one this process connects as: the fleet's image commands connect as the owner role, whose password is their own.
/// </summary>
public sealed class StoreDatabaseCredentials(ISecretStore store, SecretStoreOptions options, string? configuredConnectionString, TimeProvider time, TimeSpan refresh) : IDatabaseCredentials
{
    public const string ConnectionStringKey = "ConnectionStrings:Database";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _login = LoginOf(configuredConnectionString);
    private string? _password;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private volatile bool _stale = true;

    public bool Rotates => _login is not null;

    public async ValueTask<string?> PasswordAsync(CancellationToken cancellationToken)
    {
        if (!_stale && time.GetUtcNow() - _readAt < refresh)
        {
            return _password;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_stale && time.GetUtcNow() - _readAt < refresh)
            {
                return _password;
            }

            var secret = await store.ReadAsync(options.UnitSecretPath, cancellationToken).ConfigureAwait(false);
            var connectionString = secret?.GetValueOrDefault(ConnectionStringKey) ?? secret?.GetValueOrDefault("ConnectionStrings__Database");
            _password = connectionString is not null && string.Equals(LoginOf(connectionString), _login, StringComparison.Ordinal) ? Part(connectionString, "Password") : null;
            _readAt = time.GetUtcNow();
            _stale = false;
            return _password;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _stale = true;

    private static string? LoginOf(string? connectionString) => connectionString is null ? null : Part(connectionString, "Username") ?? Part(connectionString, "User ID");

    private static string? Part(string connectionString, string key)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return builder.TryGetValue(key, out var value) && value is string text && text.Length > 0 ? text : null;
    }
}
