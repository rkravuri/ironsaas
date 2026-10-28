using System.Data;
using Iron.Kernel.Data.Commands;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iron.Kernel.Data.Transactions;

/// <summary>Scoped. Opens the shared connection, starts one transaction and enlists every registered module context in it.</summary>
public sealed class UnitOfWork(SharedConnection shared, IEnumerable<RegisteredContext> contexts, IServiceProvider provider, IDatabaseCredentials credentials, ILogger<UnitOfWork> logger) : IUnitOfWork, IAsyncDisposable
{
    private readonly List<DbContext> _enlisted = [];
    private NpgsqlTransaction? _transaction;
    private int _depth;
    private bool _rollbackOnly;

    public bool IsActive => _transaction is not null;

    public async Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            var connection = shared.Connection;
            if (connection.State != ConnectionState.Open)
            {
                await DatabaseConnections.OpenAsync(connection, credentials, cancellationToken).ConfigureAwait(false);
            }

            _transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            _rollbackOnly = false;
            foreach (var registration in contexts)
            {
                var context = (DbContext)provider.GetRequiredService(registration.ContextType);
                await context.Database.UseTransactionAsync(_transaction, cancellationToken).ConfigureAwait(false);
                _enlisted.Add(context);
            }
        }

        _depth++;
        return new Scope(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            logger.LogWarning("A unit of work was still open when its scope ended; rolling back.");
            await FinishAsync(commit: false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task CommitScopeAsync(CancellationToken cancellationToken)
    {
        if (_depth > 1)
        {
            return; // an inner scope: the outermost one commits
        }

        if (_rollbackOnly)
        {
            throw new InvalidOperationException("An inner scope of this unit of work ended without Commit; the transaction can only roll back.");
        }

        await FinishAsync(commit: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task EndScopeAsync(bool committed)
    {
        if (!committed)
        {
            _rollbackOnly = true;
        }

        _depth--;
        if (_depth == 0 && _transaction is not null)
        {
            await FinishAsync(commit: false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task FinishAsync(bool commit, CancellationToken cancellationToken)
    {
        var transaction = _transaction;
        if (transaction is null)
        {
            return;
        }

        _transaction = null;
        try
        {
            if (commit)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var context in _enlisted)
            {
                context.Database.UseTransaction(null);
            }

            _enlisted.Clear();
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Scope(UnitOfWork owner) : ITransactionScope
    {
        private bool _committed;
        private bool _ended;

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (_committed)
            {
                return;
            }

            await owner.CommitScopeAsync(cancellationToken).ConfigureAwait(false);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            await owner.EndScopeAsync(_committed).ConfigureAwait(false);
        }
    }
}
