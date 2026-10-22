using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// A session as the lists show it: whose, on what, from where, since when, and how it signed in (TF-022). Its own rule: a
/// person lists their own, and the holder of <c>identity.sessions.manage</c> everyone's.
/// </summary>
public sealed class SessionRow : IOwnAccessRule
{
    public Guid Id { get; init; }

    public Guid PrincipalId { get; init; }

    public string LoginId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? Browser { get; init; }

    public string? Platform { get; init; }

    public string? City { get; init; }

    public string? Country { get; init; }

    public string Method { get; init; } = SessionMethods.Password;

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset LastActivityAt { get; init; }

    public string? ImpersonatorName { get; init; }

    public DateTimeOffset? EndedAt { get; init; }
}

/// <summary>One UTC day of the summary: sessions started, the most people signed in at once, and sessions an administrator ended.</summary>
public sealed record SessionDay(DateOnly Date, int Started, int PeakPeople, int EndedByAdministrator);

/// <summary>The console's summary (TF-022): people signed in now, and the last days.</summary>
public sealed record SessionSummary(int SignedInNow, IReadOnlyList<SessionDay> Days);

/// <summary>
/// The sessions console (TF-022, TF-029, ADR-0049): the live sessions, the summary, ending a session with a reason, and
/// revoking everything of a principal or of the whole tenant. Each change and its audit event are one transaction, and
/// the person whose session or access it was sees it in their security activity.
/// </summary>
public sealed class SessionConsole(
    IdentityDbContext context,
    SessionService sessions,
    SecurityEventLog securityEvents,
    RevocationTimes revocations,
    IdentityOptions options,
    TenantContext tenant,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    TimeProvider time,
    ILogger<SessionConsole> logger)
{
    /// <summary>How many days the summary goes back at most.</summary>
    public const int MaxDays = 31;

    /// <summary>The live sessions, with their person, for the lists; ended ones are left out.</summary>
    public IQueryable<SessionRow> Live() =>
        context.Sessions.AsNoTracking()
            .Where(s => s.EndedAt == null)
            .Join(context.Principals.AsNoTracking(), s => s.PrincipalId, p => p.Id, (s, p) => new SessionRow
            {
                Id = s.Id,
                PrincipalId = s.PrincipalId,
                LoginId = p.LoginId,
                DisplayName = p.DisplayName,
                Browser = s.Browser,
                Platform = s.Platform,
                City = s.City,
                Country = s.Country,
                Method = s.Method,
                StartedAt = s.CreatedAt,
                LastActivityAt = s.LastActivityAt,
                ImpersonatorName = s.ImpersonatorName,
                EndedAt = s.EndedAt,
            });

    /// <summary>
    /// The summary of the last <paramref name="days"/> UTC days, today included: sessions started, the peak of people
    /// signed in at once, and sessions an administrator ended; and how many people are signed in now. A session counts
    /// until its end, or until its idle or absolute limit when nobody ended it sooner.
    /// </summary>
    public async Task<SessionSummary> SummaryAsync(int days, CancellationToken cancellationToken)
    {
        days = Math.Clamp(days, 1, MaxDays);
        await sessions.EndExpiredAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var first = today.AddDays(1 - days);
        var from = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var rows = await context.Sessions.AsNoTracking()
            .Where(s => s.CreatedAt < now && (s.EndedAt == null || s.EndedAt >= from))
            .Select(s => new { s.PrincipalId, s.CreatedAt, s.EndedAt, s.EndReason, s.LastActivityAt, s.ExpiresAt, s.IdleMinutes })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var intervals = rows.Select(r =>
        {
            var idle = r.IdleMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : options.Session.Idle;
            var limit = r.LastActivityAt + idle < r.ExpiresAt ? r.LastActivityAt + idle : r.ExpiresAt;
            var end = r.EndedAt is { } ended && ended < limit ? ended : limit;
            return (r.PrincipalId, Start: r.CreatedAt, End: end < now || r.EndedAt is not null ? end : now);
        }).ToList();

        var signedInNow = rows.Where(r => r.EndedAt is null).Select(r => r.PrincipalId).Distinct().Count();
        var peaks = PeakPeoplePerDay(intervals, first, today);
        var result = new List<SessionDay>();
        for (var day = first; day <= today; day = day.AddDays(1))
        {
            var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var end = start.AddDays(1);
            result.Add(new SessionDay(
                day,
                rows.Count(r => r.CreatedAt >= start && r.CreatedAt < end),
                peaks.GetValueOrDefault(day),
                rows.Count(r => r.EndReason == SessionEndReasons.EndedByAdministrator && r.EndedAt >= start && r.EndedAt < end)));
        }

        return new SessionSummary(signedInNow, result);
    }

    /// <summary>
    /// The most people signed in at once on each day: a person's overlapping sessions are one presence, then a sweep over
    /// the starts and ends, taking the count at each day's start and after each change within it.
    /// </summary>
    public static Dictionary<DateOnly, int> PeakPeoplePerDay(IReadOnlyList<(Guid PrincipalId, DateTimeOffset Start, DateTimeOffset End)> intervals, DateOnly first, DateOnly last)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var presences = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var person in intervals.Where(i => i.End > i.Start).GroupBy(i => i.PrincipalId))
        {
            DateTimeOffset? start = null;
            DateTimeOffset end = default;
            foreach (var interval in person.OrderBy(i => i.Start))
            {
                if (start is null)
                {
                    (start, end) = (interval.Start, interval.End);
                }
                else if (interval.Start <= end)
                {
                    end = interval.End > end ? interval.End : end;
                }
                else
                {
                    presences.Add((start.Value, end));
                    (start, end) = (interval.Start, interval.End);
                }
            }

            if (start is not null)
            {
                presences.Add((start.Value, end));
            }
        }

        // Ends before starts at the same moment: a person who signs out as another signs in is not counted twice.
        var changes = presences.SelectMany(p => new[] { (At: p.Start, Delta: 1), (At: p.End, Delta: -1) }).OrderBy(c => c.At).ThenBy(c => c.Delta).ToList();
        var peaks = new Dictionary<DateOnly, int>();
        var count = 0;
        var index = 0;
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var end = start.AddDays(1);
            while (index < changes.Count && changes[index].At <= start)
            {
                count += changes[index].Delta;
                index++;
            }

            var peak = count;
            while (index < changes.Count && changes[index].At < end)
            {
                count += changes[index].Delta;
                peak = Math.Max(peak, count);
                index++;
            }

            peaks[day] = peak;
        }

        return peaks;
    }

    /// <summary>
    /// An administrator ends someone's session, with a reason (TF-022 AC1): the session's next request is refused, the
    /// audit trail names who ended it and why, and the person sees it in their security activity.
    /// </summary>
    public async Task<Result> EndAsync(Guid sessionId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var session = await context.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Error.NotFound(IdentityErrors.SessionNotFound, "There is no session with this id.");
        }

        if (session.HasEnded || session.ExpiryReason(time.GetUtcNow(), options.Session.Idle) is not null)
        {
            return Error.Conflict(IdentityErrors.SessionEnded, "This session has ended already.");
        }

        var principal = await context.Principals.SingleAsync(p => p.Id == session.PrincipalId, cancellationToken).ConfigureAwait(false);
        var why = reason.Trim();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        session.End(time.GetUtcNow(), SessionEndReasons.EndedByAdministrator, actor.Id);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        // The person's security event is the audit event too (identity.session_ended), naming the administrator, the reason and the session.
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["ended_by"] = "administrator", ["session_id"] = session.Id.ToString("D") };
        await securityEvents.RecordAsync(principal, SecurityEventKinds.SessionEnded, SecurityEventActors.Administrator, await ActorNameAsync(cancellationToken).ConfigureAwait(false), why, details, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Session {SessionId} of principal {PrincipalId} ended by an administrator.", session.Id, principal.Id);
        return Result.Success();
    }

    /// <summary>A person ends one of their own other sessions from My profile (TF-022): not the one they ask from, which signing out ends.</summary>
    public async Task<Result> EndOwnAsync(Guid principalId, Guid currentSessionId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await context.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.PrincipalId == principalId && s.Method != SessionMethods.Impersonation, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Error.NotFound(IdentityErrors.SessionNotFound, "You have no session with this id.");
        }

        if (session.Id == currentSessionId)
        {
            return Error.Conflict(IdentityErrors.SessionCurrent, "This is the session you are using now. Sign out to end it.");
        }

        if (session.HasEnded)
        {
            return Error.Conflict(IdentityErrors.SessionEnded, "This session has ended already.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        session.End(time.GetUtcNow(), SessionEndReasons.EndedByPerson, principalId);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.SessionEnded, AuditOutcome.Success, principalId, null, new Dictionary<string, string>(StringComparer.Ordinal) { ["ended_by"] = "self" }, "session", session.Id, SessionEndReasons.EndedByPerson), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>"Sign out everywhere else": every other session of the person ends (TF-022), audited as one event with the count.</summary>
    public async Task<int> EndOthersAsync(Guid principalId, Guid currentSessionId, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var others = await context.Sessions.Where(s => s.PrincipalId == principalId && s.EndedAt == null && s.Id != currentSessionId && s.Method != SessionMethods.Impersonation).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        foreach (var session in others)
        {
            session.End(now, SessionEndReasons.EndedByPerson, principalId);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["ended_by"] = "self", ["sessions_ended"] = others.Count.ToString(CultureInfo.InvariantCulture) };
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.SessionEnded, AuditOutcome.Success, principalId, null, details, "session", currentSessionId, "signed out everywhere else"), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return others.Count;
    }

    /// <summary>
    /// Everything of a person or a service account stops (TF-029, ADR-0049): every session ends now, and no token issued
    /// until now is accepted any more, from the next request. Nobody revokes themselves here: they sign out everywhere.
    /// The principal sees it in their security activity.
    /// </summary>
    public async Task<Result<int>> RevokePrincipalAsync(Principal principal, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (principal.Id == actor.Id)
        {
            return Error.Conflict(IdentityErrors.UserSelf, "You cannot revoke your own access. Sign out everywhere in My profile instead.");
        }

        var why = reason.Trim();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        principal.RevokeAll(time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var ended = await sessions.EndAllAsync(principal.Id, SessionEndReasons.Revoked, cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["sessions_ended"] = ended.ToString(CultureInfo.InvariantCulture), ["tokens"] = "refused" };
        await securityEvents.RecordAsync(principal, SecurityEventKinds.AccessRevoked, SecurityEventActors.Administrator, await ActorNameAsync(cancellationToken).ConfigureAwait(false), why, details, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Everything of principal {PrincipalId} revoked: {Ended} session(s) ended, earlier tokens refused.", principal.Id, ended);
        return ended;
    }

    /// <summary>
    /// Everything of the whole tenant stops (TF-029 AC1, ADR-0049), after an incident: the administrator types the tenant's
    /// identifier to confirm. Every session ends now, the asker's own included, and no token issued until now is accepted:
    /// at once in this process, within a minute in every other. Everyone signs in again.
    /// </summary>
    public async Task<Result<int>> RevokeTenantAsync(string reason, string confirmation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!string.Equals(confirmation?.Trim(), tenant.Id.Value, StringComparison.Ordinal))
        {
            return Error.Validation(IdentityErrors.RevocationConfirmation, $"Type the tenant's identifier, {tenant.Id.Value}, to confirm.", new FieldError("confirmation", "mismatch", $"Type {tenant.Id.Value}."));
        }

        var why = reason.Trim();
        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.TenantRevocations.Add(new TenantRevocation(now, why));
        var live = await context.Sessions.Where(s => s.EndedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var session in live)
        {
            session.End(now, SessionEndReasons.Revoked, actor.Id);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["sessions_ended"] = live.Count.ToString(CultureInfo.InvariantCulture), ["valid_after"] = now.ToString("O", CultureInfo.InvariantCulture) };
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.TenantRevoked, AuditOutcome.Success, actor.Id, tenant.Id.Value, details, Reason: why, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        revocations.Revoked(now);
        logger.LogWarning("Every session and token of tenant {Tenant} revoked: {Ended} session(s) ended.", tenant.Id.Value, live.Count);
        return live.Count;
    }

    private async Task<string?> ActorNameAsync(CancellationToken cancellationToken) =>
        await context.Principals.AsNoTracking().Where(p => p.Id == actor.Id).Select(p => p.DisplayName).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
}
