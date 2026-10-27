namespace Iron.Kernel.Audit.Contracts;

/// <summary>What happened to an anchor offered to a store.</summary>
public enum AuditAnchorAppend
{
    /// <summary>The store did not have it, and has it now.</summary>
    Added,

    /// <summary>The store had the same anchor already: an earlier pass sent it.</summary>
    AlreadyThere,

    /// <summary>The store holds another hash for that place, and keeps it: the verification reports the difference.</summary>
    Conflict,
}

/// <summary>
/// Where a host keeps its own chain's anchors outside its database (TF-061, ADR-0053): the control plane's go to object
/// storage it may add to and never change or delete from. A unit's go to the control plane instead (ADR-0050). The store
/// is read by the verification, with its own access, never through the host whose chain it checks.
/// </summary>
public interface IAuditAnchorStore
{
    /// <summary>The highest sealed position the store holds, or 0.</summary>
    Task<long> LatestSealAsync(CancellationToken cancellationToken);

    /// <summary>Adds an anchor; an anchor for a place the store already holds is never replaced.</summary>
    Task<AuditAnchorAppend> AppendAsync(AuditAnchor anchor, CancellationToken cancellationToken);

    /// <summary>Every anchor from a position on, in position order.</summary>
    Task<IReadOnlyList<AuditAnchor>> ReadAsync(long fromPosition, CancellationToken cancellationToken);
}
