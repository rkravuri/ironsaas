using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Iron.Kernel.Data.Transactions;

/// <summary>
/// Opens a connection when the database password may have just been rotated (TF-073 AC2, ADR-0054). The fleet writes the
/// new password to the store and then sets it in the database; a connection refused its password in between, or with the
/// password this process read before, has the credentials read the store again and tries again, a little later each
/// time, so no request fails while the password changes. Connections already open are not affected by a rotation.
/// </summary>
public static class DatabaseConnections
{
    public const int MaxAttempts = 6;

    public static async Task OpenAsync(DbConnection connection, IDatabaseCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credentials);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (PostgresException exception) when (IsRefusedPassword(exception) && credentials.Rotates && attempt < MaxAttempts)
            {
                credentials.Invalidate();
                await Task.Delay(Pause(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static void Open(DbConnection connection, IDatabaseCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credentials);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                connection.Open();
                return;
            }
            catch (PostgresException exception) when (IsRefusedPassword(exception) && credentials.Rotates && attempt < MaxAttempts)
            {
                credentials.Invalidate();
                Thread.Sleep(Pause(attempt));
            }
        }
    }

    /// <summary>A password PostgreSQL refused: 28P01 (wrong password) or 28000 (no such login, or not allowed).</summary>
    public static bool IsRefusedPassword(PostgresException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SqlState is PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification;
    }

    // 100, 200, 400, 800 and 1,600 ms: about three seconds in all, far longer than the fleet takes between its two writes.
    private static TimeSpan Pause(int attempt) => TimeSpan.FromMilliseconds(100 * (1 << (attempt - 1)));
}

/// <summary>Opens Entity Framework's connections through <see cref="DatabaseConnections"/> when the password rotates.</summary>
public sealed class CredentialRetryInterceptor(IDatabaseCredentials credentials) : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (result.IsSuppressed || !credentials.Rotates)
        {
            return result;
        }

        DatabaseConnections.Open(connection, credentials);
        return InterceptionResult.Suppress();
    }

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (result.IsSuppressed || !credentials.Rotates)
        {
            return result;
        }

        await DatabaseConnections.OpenAsync(connection, credentials, cancellationToken).ConfigureAwait(false);
        return InterceptionResult.Suppress();
    }
}
