namespace Iron.Kernel.Authorization.Contracts;

/// <summary>
/// A delegation of duties ended before its time (TF-037): by a person (<c>ended</c>), or because a person on either side
/// was deactivated (<c>person_deactivated</c>).
/// </summary>
public sealed record DelegationEnded(Guid DelegationId, [property: PersonalData] Guid DelegatorId, [property: PersonalData] Guid DelegateId, string Cause)
{
    public const string ByPerson = "ended";
    public const string PersonDeactivated = "person_deactivated";
}

/// <summary>The domain events Authorization publishes (TF-110, ADR-0060), which other modules subscribe to through these declarations.</summary>
public static class AuthorizationEvents
{
    public const string Owner = "Authorization module";

    /// <summary>Identity stops the delegate's sessions acting for the delegator on it, at once rather than at their next request.</summary>
    public static readonly EventType<DelegationEnded> DelegationEnded = new(
        "authz.delegation.ended", 1, "delegation", "A delegation of duties ended before its time, by a person or because a person on either side was deactivated.", Owner);

    public static IReadOnlyList<EventType> All => [DelegationEnded];
}
