using Iron.Kernel.Identity.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// The half-done authentications (ADR-0047): a browser that is not signed in holds one by a random token in the
/// <see cref="CookieName"/> cookie; a signed-in person's belongs to their session. Each lives five minutes, an enrolment's
/// fifteen; an expired one is never found, and starting a new one deletes the expired ones, so they do not pile up.
/// </summary>
public sealed class AuthenticationChallenges(IdentityDbContext context, TimeProvider time)
{
    /// <summary>The cookie of a sign-in under way: HttpOnly, Secure, SameSite=Strict, for the sign-in's paths only.</summary>
    public const string CookieName = "iron.sign_in";

    public async Task<(AuthenticationChallenge Challenge, string Token)> StartForBrowserAsync(string purpose, Guid? principalId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await ForgetExpiredAsync(now, cancellationToken).ConfigureAwait(false);
        var token = SecureTokens.New();
        var challenge = AuthenticationChallenge.ForBrowser(purpose, principalId, SecureTokens.Hash(token), now);
        context.AuthenticationChallenges.Add(challenge);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (challenge, token);
    }

    /// <summary>The browser's challenge for one of <paramref name="purposes"/>, while it lives; tracked, for the next step.</summary>
    public async Task<AuthenticationChallenge?> FindForBrowserAsync(HttpContext http, IReadOnlyList<string> purposes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!http.Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = SecureTokens.Hash(token);
        var now = time.GetUtcNow();
        var challenge = await context.AuthenticationChallenges.SingleOrDefaultAsync(c => c.TokenHash == hash, cancellationToken).ConfigureAwait(false);
        return challenge is not null && !challenge.IsExpired(now) && purposes.Contains(challenge.Purpose, StringComparer.Ordinal) ? challenge : null;
    }

    /// <summary>A new challenge of the session for <paramref name="purpose"/>; an earlier one of the same purpose is replaced.</summary>
    public async Task<AuthenticationChallenge> StartForSessionAsync(string purpose, Guid principalId, Guid sessionId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await ForgetExpiredAsync(now, cancellationToken).ConfigureAwait(false);
        await context.AuthenticationChallenges.Where(c => c.SessionId == sessionId && c.Purpose == purpose).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var challenge = AuthenticationChallenge.ForSession(purpose, principalId, sessionId, now);
        context.AuthenticationChallenges.Add(challenge);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return challenge;
    }

    public async Task<AuthenticationChallenge?> FindForSessionAsync(Guid sessionId, string purpose, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var challenge = await context.AuthenticationChallenges.SingleOrDefaultAsync(c => c.SessionId == sessionId && c.Purpose == purpose, cancellationToken).ConfigureAwait(false);
        return challenge is not null && !challenge.IsExpired(now) ? challenge : null;
    }

    /// <summary>The challenge did its work, or must not be tried again.</summary>
    public async Task EndAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        context.AuthenticationChallenges.Remove(challenge);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(CancellationToken cancellationToken) => await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Ends every challenge of a person: their factors were reset, or they were deactivated.</summary>
    public Task<int> EndAllOfAsync(Guid principalId, CancellationToken cancellationToken) =>
        context.AuthenticationChallenges.Where(c => c.PrincipalId == principalId).ExecuteDeleteAsync(cancellationToken);

    /// <summary>The cookie of a browser's challenge, for as long as the challenge lives.</summary>
    public static void SetCookie(HttpContext http, string token, string purpose)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Append(CookieName, token, Options(AuthenticationChallenge.LifetimeOf(purpose)));
    }

    public static void ClearCookie(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Delete(CookieName, Options(null));
    }

    private Task<int> ForgetExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        context.AuthenticationChallenges.Where(c => c.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);

    private static CookieOptions Options(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/auth",
        IsEssential = true,
        MaxAge = maxAge,
    };
}
