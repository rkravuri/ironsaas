using Npgsql;

namespace Iron.Data.Transactions;

/// <summary>
/// The one database connection of a scope (a request, a job run, a command). Every module context of the scope uses
/// it, so a unit of work can put them all in one transaction (TF-060 AC1: the audit event is committed with the change).
/// Created closed; Entity Framework opens and closes it as it works, the unit of work opens it for a transaction.
/// </summary>
public sealed class SharedConnection : IDisposable, IAsyncDisposable
{
    public SharedConnection(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        Connection = dataSource.CreateConnection();
    }

    public NpgsqlConnection Connection { get; }

    public void Dispose() => Connection.Dispose();

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
