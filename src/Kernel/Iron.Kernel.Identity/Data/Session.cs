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
        AuthenticatedAt = now;
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

    /// <summary>
    /// When the person last proved who they are in this session: at sign-in, then at each re-authentication (TF-248,
    /// ADR-0047). A high-risk action needs it recent. Null for a session from before T3, which counts from its start.
    /// </summary>
    public DateTimeOffset? AuthenticatedAt { get; private set; }

    /// <summary>
    /// What a session's second factor was when a single sign-on provider's assertion of multi-factor authentication stood
    /// for it, the tenant trusting that provider's (ADR-0048).
    /// </summary>
    public const string ProviderMultiFactor = "provider_mfa";

    /// <summary>The second factor the session's sign-in used (<see cref="FactorKinds"/>, <c>recovery_code</c>, or <see cref="ProviderMultiFactor"/>); null for a password alone.</summary>
    public string? SecondFactor { get; private set; }

    /// <summary>The single sign-on provider the session's sign-in came through (ADR-0048); null for a password or a passkey.</summary>
    public Guid? ProviderId { get; private set; }

    public bool HasEnded => EndedAt is not null;

    /// <summary>The second factor the sign-in used, recorded when the session starts.</summary>
    public void ProvedWith(string? secondFactor) => SecondFactor = secondFactor;

    /// <summary>The sign-in came through a provider, recorded when the session starts.</summary>
    public void SignedInThrough(Guid? providerId) => ProviderId = providerId;

    /// <summary>The person proved who they are again (TF-248).</summary>
    public void Reauthenticated(DateTimeOffset now) => AuthenticatedAt = now;

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
