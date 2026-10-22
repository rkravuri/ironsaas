using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Settings.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>A session in which someone signs in as another person, and its cookie token.</summary>
public sealed record ImpersonationStarted(Session Session, string Token);

/// <summary>A one-time link used: the session it started, its cookie token, and whether it is an impersonation's.</summary>
public sealed record LinkSignIn(Principal Principal, Session Session, string Token, bool Impersonation);

/// <summary>
/// Signing in as another person (TF-025, ADR-0049). A colleague with <c>identity.impersonation.start</c> at the person's
/// unit does it from the user record with a reason and step-up, for someone whose permissions they hold; the platform's
/// operator does it through a one-time link the fleet hands over, where the tenant allows it. The impersonation is a
/// session of the person's with the impersonator named, in a cookie of its own over the impersonator's session, and ends
/// at its time or when the impersonator ends it. The person sees both in their security activity.
/// </summary>
public sealed class ImpersonationService(
    IdentityDbContext context,
    SessionService sessions,
    SecurityEventLog securityEvents,
    UserService users,
    ISettings settings,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    RequestPrincipal requestPrincipal,
    IHttpContextAccessor accessor,
    TimeProvider time,
    ILogger<ImpersonationService> logger)
{
    /// <summary>
    /// A colleague signs in as the person (TF-025): the endpoint checked the permission at the person's unit and a recent
    /// authentication; here the rest. Only a person in a shell session, only an active person who is not themselves, only
    /// someone whose permissions they hold (ADR-0046's rule, TF-025 AC4), for at most the tenant's maximum.
    /// </summary>
    public async Task<Result<ImpersonationStarted>> StartAsync(Principal person, string reason, int? minutes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(person);
        var current = requestPrincipal.Current;
        if (current is null || current.Kind != AuthenticationKind.Session)
        {
            return Error.Forbidden(IdentityErrors.ImpersonationToken, "Only a person signed in to the shell can sign in as someone else.");
        }

        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why))
        {
            return Error.Validation(IdentityErrors.ImpersonationReasonRequired, "Say why you sign in as this person: they and the auditors read it.", new FieldError("reason", "required", "Say why."));
        }

        if (person.Id == current.Id)
        {
            return Error.Conflict(IdentityErrors.ImpersonationSelf, "You cannot sign in as yourself.");
        }

        if (person.Type != PrincipalType.User || person.Status != PrincipalStatus.Active || person.BreakGlassUntil is not null)
        {
            return Error.Conflict(IdentityErrors.ImpersonationNotAllowed, $"You cannot sign in as {person.DisplayName}: only an active person of this tenant can be signed in as.");
        }

        var missing = await users.MissingPermissionsAsync(person.Id, cancellationToken).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            return Error.Forbidden(IdentityErrors.ImpersonationEscalation, $"Signing in as {person.DisplayName} would give you their permissions, so you can do it only for someone whose permissions you hold yourself. You do not hold: {string.Join(", ", missing)}.");
        }

        var duration = await DurationAsync(minutes, cancellationToken).ConfigureAwait(false);
        if (duration.IsFailure)
        {
            return duration.Error;
        }

        var endsAt = time.GetUtcNow().AddMinutes(duration.Value);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var (session, token) = await sessions.StartAsync(person, cancellationToken, started => started.Impersonated(current.Id, current.DisplayName, why, endsAt, current.SessionId), method: SessionMethods.Impersonation).ConfigureAwait(false);
        await securityEvents.RecordAsync(person, SecurityEventKinds.ImpersonationStarted, SecurityEventActors.Administrator, current.DisplayName, why, Details(session, endsAt), cancellationToken, actorId: current.Id, severity: AuditSeverity.High).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Principal {ImpersonatorId} signed in as {PrincipalId} until {EndsAt}.", current.Id, person.Id, endsAt);
        return new ImpersonationStarted(session, token);
    }

    /// <summary>The impersonator ends it now: the person's security activity says so, and the next request runs as the impersonator again.</summary>
    public async Task<Result> EndAsync(CancellationToken cancellationToken)
    {
        var current = requestPrincipal.Current;
        if (current is null || current.ImpersonatorId is not { } impersonatorId)
        {
            return Error.Conflict(IdentityErrors.ImpersonationNone, "You are not signed in as anyone else.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var session = await context.Sessions.SingleAsync(s => s.Id == current.SessionId, cancellationToken).ConfigureAwait(false);
        session.End(time.GetUtcNow(), SessionEndReasons.ImpersonationEnded);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var person = await context.Principals.SingleAsync(p => p.Id == current.Id, cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["session_id"] = session.Id.ToString("D"), ["ended_by"] = "impersonator" };
        await securityEvents.RecordAsync(person, SecurityEventKinds.ImpersonationEnded, ActorOf(session), current.ImpersonatorName, null, details, cancellationToken, actorId: impersonatorId).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// The operator's link to sign in as a person (TF-025, ADR-0049), issued by the unit's <c>impersonate</c> command for
    /// the control plane: only where the tenant allows the operator to, for an active person, for at most the tenant's
    /// maximum. The tenant's audit trail names the operator and the reason at once, whether or not the link is used.
    /// </summary>
    public async Task<Result<(string Token, DateTimeOffset ExpiresAt, Principal Person)>> IssueOperatorLinkAsync(string loginId, Guid operatorId, string operatorName, string reason, int? minutes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var allowed = await settings.ResolveAsync(IdentitySettings.ImpersonationOperatorAllowed, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        if (!IdentitySettings.ImpersonationOperatorAllowed.Read(allowed.Value))
        {
            return Error.Forbidden(IdentityErrors.ImpersonationOperatorRefused, "This tenant does not let the platform's operator sign in as its people (identity.impersonation.operator_allowed).");
        }

        var normalised = Principal.NormaliseLoginId(loginId);
        var person = await context.Principals.SingleOrDefaultAsync(p => p.LoginId == normalised, cancellationToken).ConfigureAwait(false);
        if (person is null || person.Type != PrincipalType.User || person.Status != PrincipalStatus.Active || person.BreakGlassUntil is not null)
        {
            return Error.Conflict(IdentityErrors.ImpersonationNotAllowed, $"'{normalised}' is not an active person of this tenant, so nobody can sign in as them.");
        }

        var duration = await DurationAsync(minutes, cancellationToken).ConfigureAwait(false);
        if (duration.IsFailure)
        {
            return duration.Error;
        }

        var token = SecureTokens.New();
        var link = new SignInLink(person.Id, SignInLinkPurposes.Impersonation, SecureTokens.Hash(token), time.GetUtcNow());
        link.ForImpersonation(operatorId, operatorName.Trim(), reason.Trim(), duration.Value);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.SignInLinks.Add(link);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["operator"] = operatorName.Trim(), ["minutes"] = duration.Value.ToString(CultureInfo.InvariantCulture), ["source"] = "control_plane" };
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.ImpersonationLinkIssued, AuditOutcome.Success, operatorId, person.LoginId, details, UserService.RecordType, person.Id, reason.Trim(), Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (token, link.ExpiresAt, person);
    }

    /// <summary>
    /// A one-time link from the operator's path signs its holder in, once and within its time (ADR-0049): as the person, for
    /// an impersonation, or as the break-glass account, until its time limit. Any other link gets the same refusal.
    /// </summary>
    public async Task<Result<LinkSignIn>> SignInWithLinkAsync(string code, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var hash = SecureTokens.Hash(code);
        var link = string.IsNullOrWhiteSpace(code) ? null : await context.SignInLinks.SingleOrDefaultAsync(l => l.TokenHash == hash, cancellationToken).ConfigureAwait(false);
        var person = link is null ? null : await context.Principals.SingleOrDefaultAsync(p => p.Id == link.PrincipalId, cancellationToken).ConfigureAwait(false);
        var usable = link is not null && link.IsUsable(now) && person is { Status: PrincipalStatus.Active }
            && (link.Purpose != SignInLinkPurposes.BreakGlass || person.BreakGlassUntil > now);
        if (!usable)
        {
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, person?.Id ?? Guid.Empty, person?.LoginId ?? string.Empty, Reason: "sign_in_link_invalid"), cancellationToken).ConfigureAwait(false);
            return Error.NotFound(IdentityErrors.SignInLinkInvalid, "This sign-in link does not work: it was used, it expired, or it was never issued. Ask for a new one.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        link!.Use(now);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (link.Purpose == SignInLinkPurposes.Impersonation)
        {
            var endsAt = now.AddMinutes(link.Minutes ?? IdentitySettings.ImpersonationLongestMinutes);
            var (session, token) = await sessions.StartAsync(
                person!,
                cancellationToken,
                started =>
                {
                    // From here the request runs as the person with the operator named, as the rest of the impersonation will.
                    started.Impersonated(link.ImpersonatorId!.Value, link.ImpersonatorName!, link.Reason!, endsAt, null);
                    if (accessor.HttpContext is { } http)
                    {
                        RequestPrincipal.Set(http, sessions.Describe(person!, started));
                    }
                },
                method: SessionMethods.Impersonation).ConfigureAwait(false);
            await securityEvents.RecordAsync(person!, SecurityEventKinds.ImpersonationStarted, SecurityEventActors.Operator, link.ImpersonatorName, link.Reason, Details(session, endsAt), cancellationToken, actorId: link.ImpersonatorId, severity: AuditSeverity.High).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new LinkSignIn(person!, session, token, Impersonation: true);
        }

        // The break-glass account: its session needs no enrolment, since two operators and the control plane proved who it is.
        person!.RecordLogin(now);
        var (own, ownToken) = await sessions.StartAsync(
            person,
            cancellationToken,
            started =>
            {
                if (accessor.HttpContext is { } http)
                {
                    RequestPrincipal.Set(http, sessions.Describe(person, started));
                }
            },
            method: SessionMethods.BreakGlass).ConfigureAwait(false);
        var signedIn = new Dictionary<string, string>(StringComparer.Ordinal) { ["method"] = SessionMethods.BreakGlass, ["until"] = person.BreakGlassUntil!.Value.ToString("O", CultureInfo.InvariantCulture) };
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginSucceeded, AuditOutcome.Success, person.Id, person.LoginId, signedIn, "session", own.Id, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LinkSignIn(person, own, ownToken, Impersonation: false);
    }

    /// <summary>Who ended or started an impersonation, in words for the person: the operator when it came through the control plane.</summary>
    public static string ActorOf(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.ReturnSessionId is null ? SecurityEventActors.Operator : SecurityEventActors.Administrator;
    }

    private async Task<Result<int>> DurationAsync(int? minutes, CancellationToken cancellationToken)
    {
        var resolved = await settings.ResolveAsync(IdentitySettings.ImpersonationMaxMinutes, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var maximum = IdentitySettings.ImpersonationMaxMinutes.Read(resolved.Value);
        var chosen = minutes ?? maximum;
        return chosen >= 1 && chosen <= maximum
            ? chosen
            : Error.Validation(IdentityErrors.ImpersonationDuration, $"Choose from 1 to {maximum} minutes, the most this tenant allows.", new FieldError("minutes", "out_of_range", $"From 1 to {maximum} minutes."));
    }

    private static Dictionary<string, string> Details(Session session, DateTimeOffset endsAt) => new(StringComparer.Ordinal)
    {
        ["session_id"] = session.Id.ToString("D"),
        ["until"] = endsAt.ToString("O", CultureInfo.InvariantCulture),
    };
}
