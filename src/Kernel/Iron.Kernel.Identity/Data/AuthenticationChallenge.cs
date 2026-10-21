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
}

/// <summary>
/// The state between two requests of an authentication (TF-013, TF-248, ADR-0047): after a correct password, before the
/// second factor; during an enrolment; between a WebAuthn ceremony's options and the authenticator's answer. It lives at
/// most five minutes and creates no session. A browser that is not signed in holds it by a random token in an HttpOnly
/// cookie, of which only the SHA-256 is stored; a signed-in person's belongs to their session. An authenticator app's
/// secret waiting for its first code is encrypted like a saved one.
/// </summary>
public sealed class AuthenticationChallenge : AuditedEntity
{
    public const int PurposeMaxLength = 20;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

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
        ExpiresAt = now + Lifetime;
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
    public byte[]? PendingSecret { get; private set; }

    public static AuthenticationChallenge ForBrowser(string purpose, Guid? principalId, string tokenHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        return new AuthenticationChallenge(purpose, principalId, now) { TokenHash = tokenHash };
    }

    public static AuthenticationChallenge ForSession(string purpose, Guid principalId, Guid sessionId, DateTimeOffset now) =>
        new(purpose, principalId, now) { SessionId = sessionId };

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public void Expect(string webAuthnOptions) => WebAuthnOptions = webAuthnOptions;

    public void Hold(byte[] pendingSecret) => PendingSecret = pendingSecret;
}
