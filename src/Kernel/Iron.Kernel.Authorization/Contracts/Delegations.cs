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
