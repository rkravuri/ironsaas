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
/// created_at and ends at logout, after the idle period, or at the absolute limit, whichever comes first. Both limits
/// are the tenant's when the session starts (TF-021, ADR-0036) and stay with the session: a changed setting applies to
/// new sessions, so nobody's open session shortens under them.
/// </summary>
public sealed class Session : AuditedEntity
{
    private Session()
    {
        TokenHash = string.Empty;
    }

    public Session(Guid principalId, string tokenHash, DateTimeOffset now, TimeSpan absoluteLifetime, TimeSpan idleLimit)
    {
        if (idleLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleLimit), idleLimit, "A session needs an idle limit above zero.");
        }

        if (principalId == Guid.Empty)
        {
            throw new ArgumentException("The principal id is empty.", nameof(principalId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        PrincipalId = principalId;
        TokenHash = tokenHash;
        LastActivityAt = now;
        ExpiresAt = now + absoluteLifetime;
        IdleMinutes = (int)Math.Ceiling(idleLimit.TotalMinutes);
    }

    public Guid PrincipalId { get; private set; }

    public string TokenHash { get; private set; }

    public DateTimeOffset LastActivityAt { get; private set; }

    /// <summary>The absolute limit (TF-021 AC3).</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>The idle limit the session started with, in minutes; null for a session from before A6, which keeps the configured one.</summary>
    public int? IdleMinutes { get; private set; }

    /// <summary>
    /// The person this session acts for with their delegated duties (TF-037, ADR-0037), chosen by its principal; null when
    /// it acts as itself. Cleared on the first request after the delegation ends.
    /// </summary>
    public Guid? ActingForId { get; private set; }

    /// <summary>
    /// The unit the person chose to work for (TF-222, ADR-0038); null for their home unit. New records and settings
    /// follow it, and lists keep to it and the units below it.
    /// </summary>
    public Guid? WorkingUnitId { get; private set; }

    /// <summary>The consolidated view: lists show everything the person's scopes cover (TF-222 AC1).</summary>
    public bool AllUnits { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public string? EndReason { get; private set; }

    public bool HasEnded => EndedAt is not null;

    /// <summary>The session's own idle limit, or <paramref name="fallback"/> for a session that has none.</summary>
    public TimeSpan Idle(TimeSpan fallback) => IdleMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : fallback;

    /// <summary>
    /// The moment the session ends without further activity: the idle limit or the absolute limit, whichever is first.
    /// <paramref name="fallback"/> is the idle limit of a session from before A6.
    /// </summary>
    public DateTimeOffset IdleExpiresAt(TimeSpan fallback)
    {
        var idle = Idle(fallback);
        return LastActivityAt + idle < ExpiresAt ? LastActivityAt + idle : ExpiresAt;
    }

    /// <summary>Null while alive; otherwise the reason it should end at <paramref name="now"/>.</summary>
    public string? ExpiryReason(DateTimeOffset now, TimeSpan fallback)
    {
        if (HasEnded)
        {
            return EndReason ?? SessionEndReasons.Revoked;
        }

        if (now >= ExpiresAt)
        {
            return SessionEndReasons.Absolute;
        }

        return now - LastActivityAt >= Idle(fallback) ? SessionEndReasons.Idle : null;
    }

    public void Touch(DateTimeOffset now) => LastActivityAt = now;

    public void ActFor(Guid delegatorId) => ActingForId = delegatorId;

    public void StopActing() => ActingForId = null;

    /// <summary>One unit, or all of them; the home unit is a null unit.</summary>
    public void WorkFor(Guid? unitId, bool allUnits)
    {
        WorkingUnitId = allUnits ? null : unitId;
        AllUnits = allUnits;
    }

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
