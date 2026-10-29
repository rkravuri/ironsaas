namespace Iron.Kernel.Authorization.Contracts;

/// <summary>Where a delegation stands at a moment: not yet begun, running, ended early by someone, or past its end.</summary>
public enum DelegationStatus
{
    Scheduled,
    Active,
    Ended,
    Expired,
}

/// <summary>
/// What a delegate may act with right now (ADR-0037): the delegations from one person to another that are active, taken
/// together: the duties they cover and the latest moment one of them ends.
/// </summary>
public sealed record ActiveDelegation(Guid DelegatorId, Guid DelegateId, IReadOnlyList<string> Duties, DateTimeOffset Until);

/// <summary>
/// The delegations of duties (TF-037, ADR-0037) as Identity needs them: whether a person may act for another now, and the
/// delegations a person gives and holds now, for <c>/me</c>. Authorization implements it.
/// </summary>
public interface IDelegations
{
    /// <summary>The active delegations from the delegator to the delegate, taken together; null when none is active.</summary>
    Task<ActiveDelegation?> ActiveAsync(Guid delegatorId, Guid delegateId, CancellationToken cancellationToken);

    /// <summary>Every active delegation the person gives or holds, one per pair of people.</summary>
    Task<IReadOnlyList<ActiveDelegation>> ActiveOfAsync(Guid principalId, CancellationToken cancellationToken);
}

/// <summary>
/// Whose rights a principal uses in this request (ADR-0037): the delegator's while the principal acts for them, in a
/// session that chose to or in an explanation that assumes it (ADR-0039); null otherwise. The visibility tags follow it,
/// as the grants do (ADR-0057). Authorization implements it.
/// </summary>
public interface IActingFor
{
    Task<Guid?> DelegatorOfAsync(Guid principalId, CancellationToken cancellationToken);
}
