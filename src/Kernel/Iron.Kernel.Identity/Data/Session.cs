using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>Why a session ended (TF-021, TF-029).</summary>
public static class SessionEndReasons
{
    public const string Logout = "logout";
    public const string Idle = "idle";
    public const string Absolute = "absolute";
    public const string Revoked = "revoked";
}

/// <summary>
/// A server-side session (TF-020). The cookie holds a random token; only its SHA-256 is stored. It starts at
/// created_at and ends at logout, after the idle period, or at the absolute limit, whichever comes first.
/// </summary>
public sealed class Session : AuditedEntity
{
    private Session()
    {
        TokenHash = string.Empty;
    }

    public Session(Guid principalId, string tokenHash, DateTimeOffset now, TimeSpan absoluteLifetime)
    {
        if (principalId == Guid.Empty)
        {
            throw new ArgumentException("The principal id is empty.", nameof(principalId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        PrincipalId = principalId;
        TokenHash = tokenHash;
        LastActivityAt = now;
        ExpiresAt = now + absoluteLifetime;
    }

    public Guid PrincipalId { get; private set; }

    public string TokenHash { get; private set; }

    public DateTimeOffset LastActivityAt { get; private set; }

    /// <summary>The absolute limit (TF-021 AC3).</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public string? EndReason { get; private set; }

    public bool HasEnded => EndedAt is not null;

    /// <summary>The moment the session ends without further activity: the idle limit or the absolute limit, whichever is first.</summary>
    public DateTimeOffset IdleExpiresAt(TimeSpan idle) => LastActivityAt + idle < ExpiresAt ? LastActivityAt + idle : ExpiresAt;

    /// <summary>Null while alive; otherwise the reason it should end at <paramref name="now"/>.</summary>
    public string? ExpiryReason(DateTimeOffset now, TimeSpan idle)
    {
        if (HasEnded)
        {
            return EndReason ?? SessionEndReasons.Revoked;
        }

        if (now >= ExpiresAt)
        {
            return SessionEndReasons.Absolute;
        }

        return now - LastActivityAt >= idle ? SessionEndReasons.Idle : null;
    }

    public void Touch(DateTimeOffset now) => LastActivityAt = now;

    public void End(DateTimeOffset now, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (HasEnded)
        {
            return;
        }

        EndedAt = now;
        EndReason = reason;
    }
}
