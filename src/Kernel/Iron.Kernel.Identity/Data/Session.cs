using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>Why a session ended (TF-021, TF-029).</summary>
public static class SessionEndReasons
{
    public const string Logout = "logout";
    public const string Idle = "idle";
    public const string Absolute = "absolute";
    public const string Revoked = "revoked";

    /// <summary>An administrator ended it, with a reason (TF-022 AC1).</summary>
    public const string EndedByAdministrator = "ended";

    /// <summary>The person ended it from another of their sessions, in My profile (TF-022).</summary>
    public const string EndedByPerson = "ended_by_person";

    /// <summary>A newer sign-in of the person went past the tenant's limit of sessions at once (TF-021 AC2).</summary>
    public const string Limit = "limit";

    /// <summary>The impersonation reached its time, or its impersonator ended it (TF-025 AC3).</summary>
    public const string ImpersonationEnded = "impersonation_ended";

    /// <summary>The person declined a document they must accept (TF-028 AC4).</summary>
    public const string TermsDeclined = "terms_declined";
}

/// <summary>How a session's sign-in happened (TF-022), as the sessions lists show it.</summary>
public static class SessionMethods
{
    public const int MaxLength = 20;
    public const string Password = "password";
    public const string Passkey = "passkey";
    public const string Sso = "sso";

    /// <summary>Someone else signed in as the person (TF-025, ADR-0049).</summary>
    public const string Impersonation = "impersonation";

    /// <summary>An operator's emergency access, through a one-time link (TF-026, ADR-0049).</summary>
    public const string BreakGlass = "break_glass";
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
        Method = SessionMethods.Password;
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

    /// <summary>How the sign-in happened (<see cref="SessionMethods"/>).</summary>
    public string Method { get; private set; } = SessionMethods.Password;

    /// <summary>The browser, from the user agent, as a person recognises it (TF-022); null when it could not be told.</summary>
    public string? Browser { get; private set; }

    /// <summary>The platform the browser runs on; null when it could not be told.</summary>
    public string? Platform { get; private set; }

    /// <summary>
    /// The approximate place of the sign-in, from the bundled location database (TF-022, ADR-0049): a city and a country,
    /// nothing finer. The full address is in the sign-in's audit event, kept as long as the audit trail is.
    /// </summary>
    public string? City { get; private set; }

    public string? Country { get; private set; }

    /// <summary>The administrator who ended the session, when one did (TF-022 AC1).</summary>
    public Guid? EndedById { get; private set; }

    public const int ImpersonatorNameMaxLength = 254;
    public const int ImpersonationReasonMaxLength = 500;

    /// <summary>
    /// Who is signed in as the person in this session (TF-025, ADR-0049): a colleague's principal id, or the control
    /// plane's id of the operator, who is nobody here. Null for the person's own session.
    /// </summary>
    public Guid? ImpersonatorId { get; private set; }

    /// <summary>The impersonator by name, as the band, the audit trail and the person's security activity name them.</summary>
    public string? ImpersonatorName { get; private set; }

    /// <summary>Why, in the impersonator's words: required (TF-025 AC1).</summary>
    public string? ImpersonationReason { get; private set; }

    /// <summary>When the impersonation ends by itself; the next request after it ends it (TF-025 AC3).</summary>
    public DateTimeOffset? ImpersonationEndsAt { get; private set; }

    /// <summary>The impersonator's own session, which they return to at the end; null for the operator, who has none here.</summary>
    public Guid? ReturnSessionId { get; private set; }

    public bool IsImpersonation => ImpersonatorName is not null;

    /// <summary>
    /// When the first signature of this session was made with every component (ADR-0051, Part 11 section 11.200): the
    /// series it starts lets later signatures ask for the password alone. A new session, after sign-out or after the idle
    /// limit ended this one, starts again at the first rule.
    /// </summary>
    public DateTimeOffset? SigningSince { get; private set; }

    /// <summary>
    /// When a fresh sign-in at the person's company login last confirmed them in this session (TF-248 AC6, ADR-0048):
    /// a signature by someone without a password takes it, once (ADR-0051).
    /// </summary>
    public DateTimeOffset? ProviderConfirmedAt { get; private set; }

    /// <summary>Someone signs in as the person: who, why, until when, and the session to return to.</summary>
    public void Impersonated(Guid impersonatorId, string impersonatorName, string reason, DateTimeOffset endsAt, Guid? returnSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(impersonatorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ImpersonatorId = impersonatorId;
        ImpersonatorName = impersonatorName.Length > ImpersonatorNameMaxLength ? impersonatorName[..ImpersonatorNameMaxLength] : impersonatorName;
        ImpersonationReason = reason.Length > ImpersonationReasonMaxLength ? reason[..ImpersonationReasonMaxLength] : reason;
        ImpersonationEndsAt = endsAt;
        ReturnSessionId = returnSessionId;
        Method = SessionMethods.Impersonation;
    }

    /// <summary>The sign-in's method, device and place, recorded when the session starts.</summary>
    public void Describe(string method, string? browser, string? platform, string? city, string? country)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        Method = method;
        Browser = browser;
        Platform = platform;
        City = city;
        Country = country;
    }
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

        if (ImpersonationEndsAt is { } impersonationEnds && now >= impersonationEnds)
        {
            return SessionEndReasons.ImpersonationEnded;
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

    public void End(DateTimeOffset now, string reason, Guid? endedById = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (HasEnded)
        {
            return;
        }

        EndedAt = now;
        EndReason = reason;
        EndedById = endedById;
    }

    /// <summary>
    /// When the session stopped being of use: its end, or, for one nobody ended, its idle or absolute limit if that came
    /// first. The daily summary counts people signed in by it (TF-022).
    /// </summary>
    public DateTimeOffset UsableUntil(TimeSpan fallback)
    {
        var limit = IdleExpiresAt(fallback);
        return EndedAt is { } ended && ended < limit ? ended : limit;
    }
}
