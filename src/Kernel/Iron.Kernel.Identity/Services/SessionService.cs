using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Settings.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Sessions (TF-020, TF-021, TF-029): start, resolve on every request, touch, end. A new session takes the tenant's
/// limits (<see cref="IdentitySettings"/>, ADR-0036), or the configured ones while the tenant has set none.
/// </summary>
public sealed class SessionService(IdentityDbContext context, IdentityOptions options, ISettings settings, IDelegations delegations, IWorkingUnits workingUnits, TimeProvider time, IAuditWriter audit, ILogger<SessionService> logger)
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
        var (idle, absolute) = await LimitsAsync(cancellationToken).ConfigureAwait(false);
        var session = new Session(principal.Id, SecureTokens.Hash(token), time.GetUtcNow(), absolute, idle);
        beforeSave?.Invoke(session);
        context.Sessions.Add(session);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (session, token);
    }

    /// <summary>
    /// The limits a new session takes: the tenant's settings, or, while the tenant has set none, the configured system
    /// defaults (TF-021: tenant-configurable, 60 minutes and 12 hours by default).
    /// </summary>
    public async Task<(TimeSpan Idle, TimeSpan Absolute)> LimitsAsync(CancellationToken cancellationToken)
    {
        var idle = await settings.ResolveAsync(IdentitySettings.SessionIdleMinutes, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var absolute = await settings.ResolveAsync(IdentitySettings.SessionAbsoluteHours, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        return (
            idle.Level == SettingLevel.Default ? options.Session.Idle : TimeSpan.FromMinutes(IdentitySettings.SessionIdleMinutes.Read(idle.Value)),
            absolute.Level == SettingLevel.Default ? options.Session.Absolute : TimeSpan.FromHours(IdentitySettings.SessionAbsoluteHours.Read(absolute.Value)));
    }

    public SignedInPrincipal Describe(Principal principal, Session session)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(session);
        return new SignedInPrincipal(principal.Id, principal.LoginId, principal.DisplayName, principal.Type, session.Id, session.ExpiresAt, session.IdleExpiresAt(options.Session.Idle), AuthenticationKind.Session, principal.HomeUnitId, session.CreatedAt, session.ActingForId, session.WorkingUnitId, session.AllUnits);
    }

    /// <summary>
    /// Makes the session act for a delegator (ADR-0037): only while a delegation from them to this principal is active, and
    /// only a shell session. Audited with both people.
    /// </summary>
    public async Task<Result<SignedInPrincipal>> ActForAsync(SignedInPrincipal principal, Guid delegatorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Kind == AuthenticationKind.Token)
        {
            return Error.Forbidden(IdentityErrors.ActingToken, "A token never acts for anyone. Sign in to the shell to act for someone who delegated duties to you.");
        }

        if (delegatorId == principal.Id || await delegations.ActiveAsync(delegatorId, principal.Id, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.Conflict(IdentityErrors.ActingNoDelegation, "No delegation from that person to you is active now, so you cannot act for them.");
        }

        var session = await context.Sessions.SingleAsync(s => s.Id == principal.SessionId, cancellationToken).ConfigureAwait(false);
        var before = session.ActingForId;
        session.ActFor(delegatorId);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (before is { } previous && previous != delegatorId)
        {
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.ActingStopped, AuditOutcome.Success, principal.Id, principal.LoginId, RecordType: "session", RecordId: session.Id, OnBehalfOfId: previous), cancellationToken).ConfigureAwait(false);
        }

        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.ActingStarted, AuditOutcome.Success, principal.Id, principal.LoginId, RecordType: "session", RecordId: session.Id, OnBehalfOfId: delegatorId), cancellationToken).ConfigureAwait(false);
        return principal with { ActingForId = delegatorId };
    }

    /// <summary>
    /// The unit the person works for (TF-222, ADR-0038): one of the units they may choose, or all of them where the
    /// consolidated view is open to them. Only a shell session works for a unit. Audited.
    /// </summary>
    public async Task<Result<SignedInPrincipal>> WorkForAsync(SignedInPrincipal principal, Guid? unitId, bool allUnits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Kind == AuthenticationKind.Token)
        {
            return Error.Forbidden(IdentityErrors.WorkingUnitToken, "A token works for no unit: it sees what its scopes cover. Sign in to the shell to choose a unit.");
        }

        var options = await workingUnits.OptionsAsync(principal.Id, principal.HomeUnitId, cancellationToken).ConfigureAwait(false);
        var allowed = allUnits ? options.AllUnitsAvailable : unitId is { } chosen && options.Units.Any(u => u.Id == chosen);
        if (!allowed)
        {
            return Error.Forbidden(
                IdentityErrors.WorkingUnitNotYours,
                allUnits ? "Your roles do not reach several units at once, so there is no view of all your units." : "You may not work for that unit. Choose your home unit or a unit where you hold a role.");
        }

        var stored = allUnits || unitId == principal.HomeUnitId ? null : unitId;
        var session = await context.Sessions.SingleAsync(s => s.Id == principal.SessionId, cancellationToken).ConfigureAwait(false);
        session.WorkFor(stored, allUnits);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["working_unit"] = allUnits ? "all" : unitId!.Value.ToString("D") };
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.WorkingUnitChosen, AuditOutcome.Success, principal.Id, principal.LoginId, details, "session", session.Id), cancellationToken).ConfigureAwait(false);
        return principal with { ChosenWorkingUnitId = stored, AllUnits = allUnits };
    }

    /// <summary>The session works for the home unit again: housekeeping when the chosen unit may no longer be chosen.</summary>
    public async Task ForgetWorkingUnitAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await context.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken).ConfigureAwait(false);
        if (session is not null)
        {
            session.WorkFor(null, allUnits: false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The session acts as its own principal again. Nothing to do when it was not acting.</summary>
    public async Task<SignedInPrincipal> StopActingAsync(SignedInPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Kind == AuthenticationKind.Token || principal.ActingForId is not { } delegator)
        {
            return principal;
        }

        var session = await context.Sessions.SingleAsync(s => s.Id == principal.SessionId, cancellationToken).ConfigureAwait(false);
        session.StopActing();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.ActingStopped, AuditOutcome.Success, principal.Id, principal.LoginId, RecordType: "session", RecordId: session.Id, OnBehalfOfId: delegator), cancellationToken).ConfigureAwait(false);
        return principal with { ActingForId = null };
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

        // Acting for someone holds only while a delegation from them is active. When it is not, this request runs as the
        // person themselves, the session forgets it, and the answer says so (ADR-0037).
        Guid? actingEnded = null;
        if (found.Session.ActingForId is { } delegator && await delegations.ActiveAsync(delegator, found.Principal.Id, cancellationToken).ConfigureAwait(false) is null)
        {
            found.Session.StopActing();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.ActingEnded, AuditOutcome.Success, found.Principal.Id, found.Principal.LoginId, RecordType: "session", RecordId: found.Session.Id, OnBehalfOfId: delegator), cancellationToken).ConfigureAwait(false);
            actingEnded = delegator;
        }

        if (now - found.Session.LastActivityAt >= options.Session.Touch)
        {
            found.Session.Touch(now);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return SessionResolution.Alive(Describe(found.Principal, found.Session), actingEnded);
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

    /// <summary>Ends every live session of a principal but <paramref name="keepSessionId"/>, the one asking (TF-017: "End my other sessions").</summary>
    public async Task<int> EndOthersAsync(Guid principalId, Guid? keepSessionId, string reason, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var sessions = await context.Sessions.Where(s => s.PrincipalId == principalId && s.EndedAt == null && s.Id != keepSessionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var session in sessions)
        {
            session.End(now, reason);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return sessions.Count;
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

public sealed record SessionResolution(SignedInPrincipal? Principal, string? ExpiryReason, Guid? ActingEndedFor = null)
{
    public static readonly SessionResolution Unknown = new(null, null);

    public bool IsAlive => Principal is not null;

    public bool IsExpired => ExpiryReason is not null;

    public static SessionResolution Alive(SignedInPrincipal principal, Guid? actingEndedFor = null) => new(principal, null, actingEndedFor);

    public static SessionResolution Expired(string reason) => new(null, reason);
}
