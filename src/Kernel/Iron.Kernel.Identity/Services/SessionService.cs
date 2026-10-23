using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Settings.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Sessions (TF-020, TF-021, TF-029): start, resolve on every request, touch, end. A new session takes the tenant's
/// limits (<see cref="IdentitySettings"/>, ADR-0036), or the configured ones while the tenant has set none.
/// </summary>
public sealed class SessionService(
    IdentityDbContext context,
    IdentityOptions options,
    ISettings settings,
    IDelegations delegations,
    IWorkingUnits workingUnits,
    MfaPolicy mfa,
    TimeProvider time,
    IAuditWriter audit,
    IHttpContextAccessor accessor,
    IIpLocations locations,
    RevocationTimes revocations,
    SecurityEventLog securityEvents,
    TermsService terms,
    ILogger<SessionService> logger)
{
    /// <summary>
    /// Creates the session row and returns the token for the cookie. The token is never stored. <paramref name="beforeSave"/>
    /// runs once the session has its id and before the row is written; login uses it to make the new session's principal
    /// the request's principal, so the rows it writes carry the user as created_by. The row records how the sign-in
    /// happened, the browser and platform, and the city and country of the request's address (TF-022, ADR-0049). A person's
    /// sign-in past the tenant's limit of sessions at once ends their oldest (TF-021 AC2).
    /// </summary>
    public async Task<(Session Session, string Token)> StartAsync(Principal principal, CancellationToken cancellationToken, Action<Session>? beforeSave = null, string? secondFactor = null, Guid? providerId = null, string? method = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var token = SecureTokens.New();
        var (idle, absolute) = await LimitsAsync(cancellationToken).ConfigureAwait(false);
        var session = new Session(principal.Id, SecureTokens.Hash(token), time.GetUtcNow(), absolute, idle);
        session.ProvedWith(secondFactor);
        session.SignedInThrough(providerId);
        var http = accessor.HttpContext;
        var device = DeviceNames.From(http?.Request.Headers.UserAgent.ToString());
        var place = http is null ? null : locations.Find(ClientAddress.Of(http));
        session.Describe(method ?? (providerId is null ? SessionMethods.Password : SessionMethods.Sso), device.Browser, device.Platform, place?.City, place?.Country);
        beforeSave?.Invoke(session);
        context.Sessions.Add(session);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (session.Method != SessionMethods.Impersonation)
        {
            await EnforceLimitAsync(principal, session, cancellationToken).ConfigureAwait(false);
        }

        return (session, token);
    }

    /// <summary>
    /// The tenant's limit of sessions at once (TF-021 AC2): when the person now has more live sessions than it allows, the
    /// oldest end, each audited, and each hears why at its next request. Someone signed in as the person is not the person,
    /// so those sessions neither count nor end. A service account signs in with tokens and has no sessions.
    /// </summary>
    private async Task EnforceLimitAsync(Principal principal, Session started, CancellationToken cancellationToken)
    {
        if (principal.Type != PrincipalType.User)
        {
            return;
        }

        var resolved = await settings.ResolveAsync(IdentitySettings.SessionConcurrentLimit, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var limit = IdentitySettings.SessionConcurrentLimit.Read(resolved.Value);
        if (limit <= 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        var others = await context.Sessions
            .Where(s => s.PrincipalId == principal.Id && s.EndedAt == null && s.Id != started.Id && s.Method != SessionMethods.Impersonation)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var live = others.Where(s => s.ExpiryReason(now, options.Session.Idle) is null).ToList();
        var excess = live.Count + 1 - limit;
        if (excess <= 0)
        {
            return;
        }

        foreach (var oldest in live.Take(excess))
        {
            oldest.End(now, SessionEndReasons.Limit);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var oldest in live.Take(excess))
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture), ["newer_session"] = started.Id.ToString("D") };
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.SessionEnded, AuditOutcome.Success, principal.Id, principal.LoginId, details, "session", oldest.Id, SessionEndReasons.Limit), cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("A sign-in of principal {PrincipalId} went past the limit of {Limit} sessions; {Ended} older session(s) ended.", principal.Id, limit, excess);
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
        return new SignedInPrincipal(principal.Id, principal.LoginId, principal.DisplayName, principal.Type, session.Id, session.ExpiresAt, session.IdleExpiresAt(options.Session.Idle), AuthenticationKind.Session, principal.HomeUnitId, session.CreatedAt, session.ActingForId, session.WorkingUnitId, session.AllUnits)
        {
            // A session from before T3 has no authentication time of its own: its start is the last time anyone proved it.
            AuthenticatedAt = session.AuthenticatedAt ?? session.CreatedAt,
            Method = session.Method,
            ImpersonatorId = session.ImpersonatorId,
            ImpersonatorName = session.ImpersonatorName,
            ImpersonationReason = session.ImpersonationReason,
            ImpersonationEndsAt = session.ImpersonationEndsAt,
            ImpersonatorIsOperator = session.IsImpersonation && session.ReturnSessionId is null,
        };
    }

    /// <summary>The person proved who they are again in this session (TF-248): high-risk actions may go on for a few minutes.</summary>
    public async Task<DateTimeOffset> ReauthenticatedAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var session = await context.Sessions.SingleAsync(s => s.Id == sessionId, cancellationToken).ConfigureAwait(false);
        session.Reauthenticated(now);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return now;
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

        // A break-glass account past its time limit is refused by the unit itself, whatever the fleet does (ADR-0049).
        if (reason is null && found.Principal.BreakGlassUntil is { } until && now >= until)
        {
            reason = SessionEndReasons.Revoked;
        }

        // Started before the principal's or the tenant's "valid after": revoked (TF-029, ADR-0049). A revocation ends the
        // sessions it knows at once; this catches any it did not, within the minute the tenant's time is kept.
        if (reason is null
            && (found.Session.CreatedAt < found.Principal.ValidAfter
                || found.Session.CreatedAt < await revocations.TenantValidAfterAsync(context, cancellationToken).ConfigureAwait(false)))
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
                if (found.Session.IsImpersonation && reason == SessionEndReasons.ImpersonationEnded)
                {
                    // The person sees that the time given to sign in as them ran out (TF-025 AC3, AC6).
                    var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["session_id"] = found.Session.Id.ToString("D"), ["ended_by"] = "time" };
                    await securityEvents.RecordAsync(found.Principal, SecurityEventKinds.ImpersonationEnded, SecurityEventActors.System, found.Session.ImpersonatorName, null, details, cancellationToken, actorId: SystemPrincipal.SystemId).ConfigureAwait(false);
                }
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
            // A page's reads run side by side, so two requests of one session may touch it at the same moment. Saving the
            // entity checks its version and refused the second, failing a read for nothing; a plain update of the time lets
            // both through, and the later time stays. The entity takes the time without being marked changed.
            var sessionId = found.Session.Id;
            await context.Sessions.Where(s => s.Id == sessionId && s.EndedAt == null && s.LastActivityAt < now)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastActivityAt, now), cancellationToken).ConfigureAwait(false);
            found.Session.Touch(now);
            var touched = context.Entry(found.Session).Property(s => s.LastActivityAt);
            touched.OriginalValue = now;
            touched.IsModified = false;

            // The impersonator's own session waits under the impersonation and must still be there at its end (ADR-0049).
            if (found.Session.ReturnSessionId is { } own)
            {
                await context.Sessions.Where(s => s.Id == own && s.EndedAt == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastActivityAt, now), cancellationToken).ConfigureAwait(false);
            }
        }

        // A provider's multi-factor sign-in that the tenant trusts stands for the person's own factor (ADR-0048). An
        // impersonator and a break-glass account take no step that is the person's own (ADR-0049).
        var ownSteps = !found.Session.IsImpersonation && found.Session.Method != SessionMethods.BreakGlass;

        // Terms first (TF-028 AC1): a person must accept every current version before anything else, enrolment included.
        var termsRequired = ownSteps && found.Principal.Type == PrincipalType.User && (await terms.PendingAsync(found.Principal.Id, cancellationToken).ConfigureAwait(false)).Count > 0;
        var mustEnrol = ownSteps && !termsRequired && found.Session.SecondFactor != Session.ProviderMultiFactor && await MustEnrolAsync(found.Principal, now, cancellationToken).ConfigureAwait(false);
        return SessionResolution.Alive(Describe(found.Principal, found.Session), actingEnded) with { MustEnrol = mustEnrol, TermsRequired = termsRequired };
    }

    /// <summary>
    /// Whether the tenant's policy requires a second factor of the person now, who has none, with no grace left (ADR-0047).
    /// Asked on every request of a session, so a change of the policy, or of the person's roles, takes effect at the next
    /// request rather than at the next sign-in; nobody is signed out. The first request that finds the factor required
    /// starts the grace a tenant may give, as the first sign-in would. One indexed query for a person with a factor.
    /// </summary>
    private async Task<bool> MustEnrolAsync(Principal principal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (principal.Type != PrincipalType.User)
        {
            return false;
        }

        var rules = await mfa.RulesAsync(cancellationToken).ConfigureAwait(false);
        if ((rules.RequiredFor == MfaRequirement.Nobody && !principal.Emergency)
            || await context.Factors.AnyAsync(f => f.PrincipalId == principal.Id, cancellationToken).ConfigureAwait(false)
            || !await mfa.IsRequiredAsync(principal, rules, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var since = principal.MfaRequiredSince;
        if (since is null)
        {
            // A column of bookkeeping, set once, outside the row's version: two requests at once must not fail each other.
            since = now;
            await context.Principals.Where(p => p.Id == principal.Id && p.MfaRequiredSince == null)
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.MfaRequiredSince, now), cancellationToken).ConfigureAwait(false);
        }

        var grace = MfaPolicy.GraceDaysFor(principal, rules);
        return grace == 0 || now >= since.Value.AddDays(grace);
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

    /// <summary>
    /// Ends the live sessions nobody came back to after their idle or absolute limit, as their next request would have:
    /// housekeeping before the sessions console counts who is signed in (TF-022). Each is audited as such an end is.
    /// </summary>
    public async Task<int> EndExpiredAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var shortestIdle = now - TimeSpan.FromMinutes(15); // the shortest idle limit a tenant may set
        var candidates = await context.Sessions
            .Where(s => s.EndedAt == null && (s.ExpiresAt <= now || s.LastActivityAt <= shortestIdle))
            .Join(context.Principals, s => s.PrincipalId, p => p.Id, (s, p) => new { Session = s, p.LoginId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var ended = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Session.ExpiryReason(now, options.Session.Idle) is { } reason)
            {
                candidate.Session.End(now, reason);
                await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.SessionExpired, AuditOutcome.Success, candidate.Session.PrincipalId, candidate.LoginId, Reason: reason, RecordType: "session", RecordId: candidate.Session.Id), cancellationToken).ConfigureAwait(false);
                ended++;
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ended;
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
    /// <summary>The person must enrol a second factor before anything else (ADR-0047); the session stays.</summary>
    public bool MustEnrol { get; init; }

    /// <summary>The person must accept a document's new version before anything else (TF-028 AC1, ADR-0049); the session stays.</summary>
    public bool TermsRequired { get; init; }

    public static readonly SessionResolution Unknown = new(null, null);

    public bool IsAlive => Principal is not null;

    public bool IsExpired => ExpiryReason is not null;

    public static SessionResolution Alive(SignedInPrincipal principal, Guid? actingEndedFor = null) => new(principal, null, actingEndedFor);

    public static SessionResolution Expired(string reason) => new(null, reason);
}
