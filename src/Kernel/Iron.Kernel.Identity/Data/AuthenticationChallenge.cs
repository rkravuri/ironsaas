using Iron.Kernel.Data.Encryption;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>What a half-done authentication is for (ADR-0047).</summary>
public static class ChallengePurposes
{
    /// <summary>The password was right and the person has a factor: the second step.</summary>
    public const string SecondFactor = "second_factor";

    /// <summary>The password was right and the person must have a factor and has none: enrolment, before any page.</summary>
    public const string Enrolment = "enrolment";

    /// <summary>A sign-in with a passkey and no password, where the tenant allows it; nobody is named until the passkey answers.</summary>
    public const string PasskeySignIn = "passkey_sign_in";

    /// <summary>A signed-in person adds a factor from My profile.</summary>
    public const string AddFactor = "add_factor";

    /// <summary>A signed-in person confirms it is them before a high-risk action (TF-248).</summary>
    public const string Reauthentication = "reauthentication";

    /// <summary>A sign-in through a single sign-on provider, between the provider's page and its answer (ADR-0048).</summary>
    public const string SsoSignIn = "sso_sign_in";

    /// <summary>
    /// A signed-in person confirms it is them through their provider (TF-248, ADR-0048): a fresh sign-in there, which
    /// belongs to the session it started from.
    /// </summary>
    public const string SsoReauthentication = "sso_reauthentication";
}

/// <summary>
/// The state between two requests of an authentication (TF-013, TF-248, ADR-0047): after a correct password, before the
/// second factor; during an enrolment; between a WebAuthn ceremony's options and the authenticator's answer. It creates
/// no session. It lives five minutes, and an enrolment's fifteen, since installing an app or finding a security key takes
/// longer than typing a code (the design session's review, ADR-0047). A browser that is not signed in holds it by a random token in an HttpOnly
/// cookie, of which only the SHA-256 is stored; a signed-in person's belongs to their session. An authenticator app's
/// secret waiting for its first code is encrypted like a saved one.
/// </summary>
public sealed class AuthenticationChallenge : AuditedEntity
{
    public const int PurposeMaxLength = 20;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>An enrolment's, at sign-in or in a session.</summary>
    public static readonly TimeSpan EnrolmentLifetime = TimeSpan.FromMinutes(15);

    public static TimeSpan LifetimeOf(string purpose) =>
        purpose is ChallengePurposes.Enrolment or ChallengePurposes.AddFactor ? EnrolmentLifetime : Lifetime;

    private AuthenticationChallenge()
    {
        Purpose = string.Empty;
    }

    private AuthenticationChallenge(string purpose, Guid? principalId, DateTimeOffset now)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        Purpose = purpose;
        PrincipalId = principalId;
        ExpiresAt = now + LifetimeOf(purpose);
    }

    public string Purpose { get; private set; }

    /// <summary>Who is authenticating; null for a passkey sign-in until the passkey names them.</summary>
    public Guid? PrincipalId { get; private set; }

    /// <summary>SHA-256 of the cookie's token, for a browser that is not signed in.</summary>
    public string? TokenHash { get; private set; }

    /// <summary>The session of a signed-in person.</summary>
    public Guid? SessionId { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>The options of the WebAuthn ceremony under way, as JSON: the answer is checked against them.</summary>
    public string? WebAuthnOptions { get; private set; }

    /// <summary>An authenticator app's secret waiting for its first code, encrypted.</summary>
    public EncryptedValue? HeldSecret { get; private set; }

    /// <summary>
    /// The single sign-on provider of the sign-in (ADR-0048): while the provider's page is open, and through the second
    /// step or the enrolment that may follow it, so the session records where the sign-in came from.
    /// </summary>
    public Guid? ProviderId { get; private set; }

    /// <summary>
    /// What the provider's answer is checked against, as JSON: the <c>state</c>, the <c>nonce</c>, the PKCE verifier, the
    /// redirect URI and where the person goes afterwards. Lives as long as the challenge, five minutes, and is used once.
    /// </summary>
    public string? Exchange { get; private set; }

    public static AuthenticationChallenge ForBrowser(string purpose, Guid? principalId, string tokenHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        return new AuthenticationChallenge(purpose, principalId, now) { TokenHash = tokenHash };
    }

    public static AuthenticationChallenge ForSession(string purpose, Guid principalId, Guid sessionId, DateTimeOffset now) =>
        new(purpose, principalId, now) { SessionId = sessionId };

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public void Expect(string webAuthnOptions) => WebAuthnOptions = webAuthnOptions;

    public void Hold(EncryptedValue secret) => HeldSecret = secret;

    /// <summary>A browser's challenge that belongs to a session too: a re-authentication through a provider.</summary>
    public void InSession(Guid sessionId) => SessionId = sessionId;

    /// <summary>The sign-in goes through, or came through, a provider.</summary>
    public void ThroughProvider(Guid providerId, string? exchange = null)
    {
        ProviderId = providerId;
        Exchange = exchange;
    }
}
