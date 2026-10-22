using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>What a one-time sign-in link opens (ADR-0049).</summary>
public static class SignInLinkPurposes
{
    public const int MaxLength = 20;

    /// <summary>The operator signs in as a person of the tenant, which allows it (TF-025).</summary>
    public const string Impersonation = "impersonation";

    /// <summary>An operator's emergency access through a break-glass account (TF-026).</summary>
    public const string BreakGlass = "break_glass";
}

/// <summary>
/// A link the fleet hands over to an operator through the store (ADR-0046, ADR-0049): it signs them in once, within 15
/// minutes, as the person they impersonate or as the break-glass account approved for them. Only the code's SHA-256 is
/// stored; the code is in the link's fragment, which reaches no log. For an impersonation it carries the operator, the
/// reason and the duration the control plane asked for.
/// </summary>
public sealed class SignInLink : AuditedEntity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private SignInLink()
    {
        TokenHash = string.Empty;
        Purpose = string.Empty;
    }

    public SignInLink(Guid principalId, string purpose, string tokenHash, DateTimeOffset now)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        PrincipalId = principalId;
        Purpose = purpose;
        TokenHash = tokenHash;
        ExpiresAt = now + Lifetime;
    }

    public Guid PrincipalId { get; private set; }

    public string Purpose { get; private set; }

    public string TokenHash { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    /// <summary>The operator, by the control plane's id of them, for an impersonation.</summary>
    public Guid? ImpersonatorId { get; private set; }

    public string? ImpersonatorName { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>How long the impersonation lasts once the link is used.</summary>
    public int? Minutes { get; private set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && now < ExpiresAt;

    public void ForImpersonation(Guid operatorId, string operatorName, string reason, int minutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ImpersonatorId = operatorId;
        ImpersonatorName = operatorName.Length > Session.ImpersonatorNameMaxLength ? operatorName[..Session.ImpersonatorNameMaxLength] : operatorName;
        Reason = reason.Length > Session.ImpersonationReasonMaxLength ? reason[..Session.ImpersonationReasonMaxLength] : reason;
        Minutes = minutes;
    }

    public void Use(DateTimeOffset now) => UsedAt = now;
}
