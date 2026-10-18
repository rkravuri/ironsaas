namespace Iron.Data.Transactions;

/// <summary>
/// One transaction for every module context of the current scope. A service that changes data and must record an
/// audit event in the same transaction does:
/// <code>
/// await using var transaction = await unitOfWork.BeginAsync(ct);
/// ... change rows in any module context, write the audit event ...
/// await transaction.CommitAsync(ct);
/// </code>
/// Disposing without Commit rolls back. A nested Begin joins the open transaction; an inner scope that is disposed
/// without Commit makes the whole transaction roll back.
/// </summary>
public interface IUnitOfWork
{
    bool IsActive { get; }

    Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken);
}

public interface ITransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
