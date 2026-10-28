namespace Iron.Kernel;

/// <summary>
/// Where the unit's database password comes from when a connection opens (TF-073 AC2, ADR-0054). The fleet rotates the
/// password in the store and then in the database; the unit reads it again on a short schedule and after an
/// authentication failure, so new connections use the new password without a restart and open ones go on.
/// </summary>
public interface IDatabaseCredentials
{
    /// <summary>Whether the password can change while the process runs; when not, the connection string's is used.</summary>
    bool Rotates { get; }

    /// <summary>The password for a new connection, or null for the connection string's own.</summary>
    ValueTask<string?> PasswordAsync(CancellationToken cancellationToken);

    /// <summary>A connection was refused its password: the next one reads it again first.</summary>
    void Invalidate();
}

/// <summary>The connection string's password, for a host without a store: the laptop run from source, the tests.</summary>
public sealed class ConnectionStringCredentials : IDatabaseCredentials
{
    public static readonly ConnectionStringCredentials Instance = new();

    public bool Rotates => false;

    public ValueTask<string?> PasswordAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

    public void Invalidate()
    {
    }
}
