using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>Sessions (TF-020, TF-021, TF-029): start, resolve on every request, touch, end.</summary>
public sealed class SessionService(IdentityDbContext context, IdentityOptions options, TimeProvider time, IAuditWriter audit, ILogger<SessionService> logger)
{
    /// <summary>
    /// Creates the session row and returns the token for the cookie. The token is never stored. <paramref name="beforeSave"/>
    /// runs once the session has its id and before the row is written; login uses it to make the new session's principal
    /// the request's principal, so the rows it writes carry the user as created_by.
    /// </summary>
    public async Task<(Session Session, string Token)> StartAsync(Principal principal, CancellationToken cancellationToken, Action<Session>? beforeSave = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var token = SecureTokens.New();
        var session = new Session(principal.Id, SecureTokens.Hash(token), time.GetUtcNow(), options.Session.Absolute);
        beforeSave?.Invoke(session);
        context.Sessions.Add(session);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (session, token);
    }

    public SignedInPrincipal Describe(Principal principal, Session session)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(session);
        return new SignedInPrincipal(principal.Id, principal.LoginId, principal.DisplayName, principal.Type, session.Id, session.ExpiresAt, session.IdleExpiresAt(options.Session.Idle), AuthenticationKind.Session, principal.HomeUnitId);
    }

    /// <summary>
    /// Finds the session of a cookie token. A session past its idle or absolute limit is ended here, with an audit event,
    /// and reported as expired (TF-021 AC1). A live session has its activity time refreshed at most once per touch interval.
    /// </summary>
    public async Task<SessionResolution> ResolveAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var now = time.GetUtcNow();
        var hash = SecureTokens.Hash(token);
        var found = await context.Sessions
            .Join(context.Principals, s => s.PrincipalId, p => p.Id, (s, p) => new { Session = s, Principal = p })
            .SingleOrDefaultAsync(x => x.Session.TokenHash == hash, cancellationToken)
            .ConfigureAwait(false);
        if (found is null)
        {
            return SessionResolution.Unknown;
        }

        var reason = found.Session.ExpiryReason(now, options.Session.Idle);
        if (reason is null && found.Principal.Status != PrincipalStatus.Active)
        {
            reason = SessionEndReasons.Revoked;
        }

        if (reason is not null)
        {
            if (!found.Session.HasEnded)
            {
                found.Session.End(now, reason);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.SessionExpired, AuditOutcome.Success, found.Principal.Id, found.Principal.LoginId, Reason: reason), cancellationToken).ConfigureAwait(false);
            }

            return SessionResolution.Expired(reason);
        }

        if (now - found.Session.LastActivityAt >= options.Session.Touch)
        {
            found.Session.Touch(now);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return SessionResolution.Alive(Describe(found.Principal, found.Session));
    }

    /// <summary>Ends the session now (TF-029 AC3: nothing is served on it afterwards).</summary>
    public async Task EndAsync(Guid sessionId, string reason, CancellationToken cancellationToken)
    {
        var session = await context.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.HasEnded)
        {
            return;
        }

        session.End(time.GetUtcNow(), reason);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Session {SessionId} ended: {Reason}.", sessionId, reason);
    }

    /// <summary>Ends every live session of a principal (TF-010 AC3, TF-029).</summary>
    public async Task<int> EndAllAsync(Guid principalId, string reason, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var sessions = await context.Sessions.Where(s => s.PrincipalId == principalId && s.EndedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var session in sessions)
        {
            session.End(now, reason);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return sessions.Count;
    }
}

public sealed record SessionResolution(SignedInPrincipal? Principal, string? ExpiryReason)
{
    public static readonly SessionResolution Unknown = new(null, null);

    public bool IsAlive => Principal is not null;

    public bool IsExpired => ExpiryReason is not null;

    public static SessionResolution Alive(SignedInPrincipal principal) => new(principal, null);

    public static SessionResolution Expired(string reason) => new(null, reason);
}
