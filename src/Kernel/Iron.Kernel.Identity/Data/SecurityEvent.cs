using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>
/// Something that happened to a person's sign-in (TF-019, ADR-0046): their password changed or was set by a recovery link,
/// a link was issued or used, the login was locked or unlocked, the person was deactivated or reactivated. My profile
/// lists them under "Security activity", newest first; each one is also an audit event. <see cref="Actor"/> says who did it
/// (the person, an administrator, the operator through the control plane, or the system), so the shell words it, and
/// <see cref="ActorName"/> names an administrator or the operator as they were then. Append-only; each person reads only their own.
/// </summary>
public sealed class SecurityEvent : AppendOnlyEntity, IOwnAccessRule
{
    public const int KindMaxLength = 40;
    public const int ActorMaxLength = 20;
    public const int ActorNameMaxLength = 254;
    public const int ReasonMaxLength = 500;

    private SecurityEvent()
    {
        Kind = string.Empty;
        Actor = string.Empty;
    }

    public SecurityEvent(Guid principalId, string kind, DateTimeOffset occurredAt, string actor, string? actorName, string? reason)
        : this()
    {
        if (principalId == Guid.Empty)
        {
            throw new ArgumentException("The principal id is empty.", nameof(principalId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        PrincipalId = principalId;
        Kind = kind;
        Actor = actor;
        OccurredAt = occurredAt;
        ActorName = actorName is { Length: > ActorNameMaxLength } ? actorName[..ActorNameMaxLength] : actorName;
        Reason = reason is { Length: > ReasonMaxLength } ? reason[..ReasonMaxLength] : reason;
    }

    public Guid PrincipalId { get; private set; }

    /// <summary>One of <see cref="Contracts.SecurityEventKinds"/>.</summary>
    public string Kind { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>One of <see cref="Contracts.SecurityEventActors"/>.</summary>
    public string Actor { get; private set; }

    public string? ActorName { get; private set; }

    /// <summary>Why, when the actor gave a reason: the operator's recovery always has one.</summary>
    public string? Reason { get; private set; }
}
