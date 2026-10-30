using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>A user just created: the principal and the invitation token for the link, which is never stored.</summary>
public sealed record NewUser(Principal Principal, string InvitationToken, DateTimeOffset InvitationExpiresAt);

/// <summary>A person reactivated, and the invitation opened for them when they never set a password.</summary>
public sealed record ReactivatedUser(Principal Principal, string? InvitationToken);

/// <summary>
/// Users created by an administrator (A2): a login id, a display name and a home unit, in Invited status with an open
/// invitation. The endpoint checks <c>identity.users.manage</c> at the home unit first; email delivery is S4, so the
/// caller passes the link on. The principal, its invitation and the audit events are one transaction. The same
/// administrator unlocks a person's login, issues their recovery link, resets their second factors, deactivates and
/// reactivates them (TF-010, TF-012, TF-013, TF-017, ADR-0046, ADR-0047); each is a security event of the person's.
/// </summary>
public sealed class UserService(
    IdentityDbContext context,
    InvitationService invitations,
    PasswordService passwords,
    LoginThrottles throttles,
    SessionService sessions,
    SecurityEventLog securityEvents,
    FactorService factors,
    SsoPeople ssoPeople,
    IOrganisationUnits units,
    IRoleAssignments roleAssignments,
    ITenantAdministrators administrators,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor)
{
    public const string RecordType = IdentityRecordTypes.Principal;

    /// <summary>
    /// A recovery link for a person, issued by an administrator who may manage them (the endpoint checked
    /// <c>identity.users.manage</c> at their home unit). The link lets its holder sign in as the person, so the
    /// administrator must hold every permission the person holds, tenant-wide and anywhere, as for granting a role
    /// (ADR-0031 rule 1); otherwise a unit's administrator could take over a tenant administrator homed in that unit.
    /// </summary>
    public async Task<Result<RecoveryLink>> IssueRecoveryAsync(Principal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Type != PrincipalType.User || principal.Status != PrincipalStatus.Active || principal.PasswordHash is null)
        {
            return Error.Conflict(IdentityErrors.RecoveryNotAllowed, $"{principal.DisplayName} cannot sign in with a password now ({principal.Status.ToString().ToLowerInvariant()}), so a recovery link would not help. Reactivate them, or send a new invitation.");
        }

        if ((await ssoPeople.SsoOnlyAsync(principal, cancellationToken).ConfigureAwait(false)).SsoOnly)
        {
            return Error.Conflict(IdentityErrors.RecoverySsoOnly, $"{principal.DisplayName} signs in only with their company login, so a recovery link would not help. Their provider recovers their account.");
        }

        var missing = await MissingPermissionsAsync(principal.Id, cancellationToken).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            return Error.Forbidden(IdentityErrors.RecoveryEscalation, $"A recovery link lets its holder sign in as {principal.DisplayName}, so you can issue one only for someone whose permissions you hold yourself. You do not hold: {string.Join(", ", missing)}.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var token = await passwords.OpenRecoveryAsync(principal, SecurityEventActors.Administrator, await ActorNameAsync(cancellationToken).ConfigureAwait(false), null, null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RecoveryLink(token, principal.RecoveryExpiresAt!.Value);
    }

    /// <summary>
    /// An administrator resets a person's second factors (TF-013 AC3, ADR-0047): a person who lost their device signs in
    /// with their password and enrols again at once. Like a recovery link it lets someone else in as the person, so the
    /// administrator must hold every permission the person holds (ADR-0046's rule); a reason is required, the endpoint
    /// asks for a recent authentication, and every session of the person ends. Nobody resets their own: they remove a
    /// factor in My profile.
    /// </summary>
    public async Task<Result<Principal>> ResetFactorsAsync(Principal principal, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (principal.Type != PrincipalType.User)
        {
            return Error.Conflict(IdentityErrors.UserStatusConflict, $"{principal.DisplayName} is a service account, which signs in with its client credentials and has no second factor.");
        }

        if (principal.Id == actor.Id)
        {
            return Error.Conflict(IdentityErrors.UserSelf, "You cannot reset your own factors. Remove a factor in My profile, or ask another administrator.");
        }

        var missing = await MissingPermissionsAsync(principal.Id, cancellationToken).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            return Error.Forbidden(IdentityErrors.FactorResetEscalation, $"Resetting the factors lets {principal.DisplayName}'s password alone sign them in, so you can do it only for someone whose permissions you hold yourself. You do not hold: {string.Join(", ", missing)}.");
        }

        if (!await factors.HasAnyAsync(principal.Id, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(IdentityErrors.FactorNone, $"{principal.DisplayName} has no second factor to reset.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var ended = await sessions.EndAllAsync(principal.Id, SessionEndReasons.Revoked, cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["sessions_ended"] = ended.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        await factors.ResetAsync(principal, SecurityEventActors.Administrator, await ActorNameAsync(cancellationToken).ConfigureAwait(false), reason.Trim(), details, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return principal;
    }

    /// <summary>
    /// Marks a person to sign in only through a single sign-on provider, or lets them use a password again (TF-014 AC2,
    /// ADR-0048). Only where a provider can sign them in, a link of theirs or a provider of their email's domain, or they
    /// would be locked out. Their sessions stay; the mark holds at their next password sign-in.
    /// </summary>
    public async Task<Result<Principal>> SetSsoOnlyAsync(Principal principal, bool ssoOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Type != PrincipalType.User)
        {
            return Error.Conflict(IdentityErrors.UserStatusConflict, $"{principal.DisplayName} is a service account, which signs in with its client credentials.");
        }

        if (principal.SsoOnly == ssoOnly)
        {
            return principal;
        }

        if (ssoOnly && principal.Emergency)
        {
            return Error.Conflict(IdentityErrors.UserEmergency, $"{principal.DisplayName}'s account is an emergency account, for when the company login is down, so it cannot be single sign-on only. Remove the emergency mark first.");
        }

        if (ssoOnly
            && (await ssoPeople.LinkedProvidersAsync(principal.Id, cancellationToken).ConfigureAwait(false)).Count == 0
            && await ssoPeople.ForEmailAsync(principal.Email ?? principal.LoginId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.Conflict(IdentityErrors.UserSsoUnreachable, $"No single sign-on provider can sign {principal.DisplayName} in: they are linked to none, and no provider signs in people of their email's domain. Connect one first.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        principal.MarkSsoOnly(ssoOnly);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await securityEvents.RecordAsync(principal, ssoOnly ? SecurityEventKinds.SsoOnlySet : SecurityEventKinds.SsoOnlyCleared, SecurityEventActors.Administrator, await ActorNameAsync(cancellationToken).ConfigureAwait(false), null, null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return principal;
    }

    /// <summary>The most emergency accounts a tenant may have (ADR-0049).</summary>
    public const int EmergencyLimit = 2;

    /// <summary>
    /// Marks a person's account as one of the tenant's emergency accounts, or removes the mark (ADR-0049): at most two, each
    /// an active local account with a password and a second factor, and not single sign-on only. Only for someone whose
    /// permissions the administrator holds, since the mark lets the account sign in when everyone else cannot.
    /// </summary>
    public async Task<Result<Principal>> SetEmergencyAsync(Principal principal, bool emergency, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Emergency == emergency)
        {
            return principal;
        }

        if (emergency)
        {
            if (principal.Type != PrincipalType.User || principal.Status != PrincipalStatus.Active || principal.PasswordHash is null || principal.BreakGlassUntil is not null)
            {
                return Error.Conflict(IdentityErrors.EmergencyNotLocal, $"{principal.DisplayName} cannot be an emergency account: it must be an active person who signs in with a password of their own here.");
            }

            if (principal.SsoOnly)
            {
                return Error.Conflict(IdentityErrors.UserEmergency, $"{principal.DisplayName} signs in only with their company login. Let them use a password again first.");
            }

            if (!await factors.HasAnyAsync(principal.Id, cancellationToken).ConfigureAwait(false))
            {
                return Error.Conflict(IdentityErrors.EmergencyNeedsFactor, $"An emergency account must have a second factor. {principal.DisplayName} has none: they enrol one in My profile first.");
            }

            if (await context.Principals.CountAsync(p => p.Emergency, cancellationToken).ConfigureAwait(false) >= EmergencyLimit)
            {
                return Error.Conflict(IdentityErrors.EmergencyLimit, $"This tenant has its {EmergencyLimit} emergency accounts already. Remove the mark from one first.");
            }
        }

        var missing = await MissingPermissionsAsync(principal.Id, cancellationToken).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            return Error.Forbidden(IdentityErrors.RecoveryEscalation, $"You can change the emergency mark only for someone whose permissions you hold yourself. You do not hold: {string.Join(", ", missing)}.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        principal.MarkEmergency(emergency);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await securityEvents.RecordAsync(principal, emergency ? SecurityEventKinds.EmergencySet : SecurityEventKinds.EmergencyCleared, SecurityEventActors.Administrator, await ActorNameAsync(cancellationToken).ConfigureAwait(false), null, null, cancellationToken, severity: AuditSeverity.High).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return principal;
    }

    /// <summary>Whether the actor holds every permission the person holds, the rule for a recovery link and a factor reset (ADR-0046).</summary>
    public async Task<bool> MayTakeOverAsync(Guid principalId, CancellationToken cancellationToken) =>
        principalId != actor.Id && (await MissingPermissionsAsync(principalId, cancellationToken).ConfigureAwait(false)).Count == 0;

    /// <summary>Lifts the lock on a person's login identifier (TF-012 AC2).</summary>
    public async Task<Result<Principal>> UnlockAsync(Principal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        if (!await throttles.ClearAsync(principal.LoginId, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(IdentityErrors.UserStatusConflict, $"The login of {principal.DisplayName} is not locked.");
        }

        await securityEvents.RecordAsync(principal, SecurityEventKinds.LoginUnlocked, SecurityEventActors.Administrator, await ActorNameAsync(cancellationToken).ConfigureAwait(false), null, null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return principal;
    }

    /// <summary>
    /// Deactivates a person (TF-010 AC3): they cannot sign in, every session ends now, their tokens are refused from the
    /// next request, and an open invitation or recovery link stops working. Nobody deactivates themselves, and the last
    /// active tenant administrator stays, under the same lock as the role's removal (A2).
    /// </summary>
    public async Task<Result<Principal>> DeactivateAsync(Principal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Id == actor.Id)
        {
            return Error.Conflict(IdentityErrors.UserSelf, "You cannot deactivate yourself. Ask another administrator.");
        }

        if (principal.IsDeactivated)
        {
            return Error.Conflict(IdentityErrors.UserStatusConflict, $"{principal.DisplayName} is deactivated already.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var holders = await administrators.LockAndListAsync(cancellationToken).ConfigureAwait(false);
        if (holders.Contains(principal.Id))
        {
            var others = holders.Where(id => id != principal.Id).ToList();
            var activeOthers = await context.Principals.CountAsync(p => others.Contains(p.Id) && p.Status == PrincipalStatus.Active, cancellationToken).ConfigureAwait(false);
            if (activeOthers == 0)
            {
                return Error.Conflict(IdentityErrors.UserLastAdministrator, $"{principal.DisplayName} is the last active tenant administrator. Make someone else tenant administrator first, then deactivate them.");
            }
        }

        principal.Deactivate();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var ended = await sessions.EndAllAsync(principal.Id, SessionEndReasons.Revoked, cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["sessions_ended"] = ended.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        await securityEvents.RecordAsync(principal, SecurityEventKinds.UserDeactivated, SecurityEventActors.Administrator, await ActorNameAsync(cancellationToken).ConfigureAwait(false), null, details, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return principal;
    }

    /// <summary>
    /// Reactivates a deactivated person: Active with the password they had, or Invited with a new invitation when they
    /// never set one, whose link the answer carries.
    /// </summary>
    public async Task<Result<ReactivatedUser>> ReactivateAsync(Principal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!principal.IsDeactivated)
        {
            return Error.Conflict(IdentityErrors.UserStatusConflict, $"{principal.DisplayName} is not deactivated.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        principal.Reactivate();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var invitation = principal.Status == PrincipalStatus.Invited ? await invitations.InviteAsync(principal, cancellationToken).ConfigureAwait(false) : null;
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["status"] = principal.Status.ToString().ToLowerInvariant() };
        await securityEvents.RecordAsync(principal, SecurityEventKinds.UserReactivated, SecurityEventActors.Administrator, await ActorNameAsync(cancellationToken).ConfigureAwait(false), null, details, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ReactivatedUser(principal, invitation);
    }

    /// <summary>
    /// The permissions the person holds, tenant-wide or anywhere, that the actor does not hold as widely: the rule for a
    /// recovery link, a factor reset (ADR-0046) and an impersonation (ADR-0049).
    /// </summary>
    public async Task<IReadOnlyList<string>> MissingPermissionsAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var theirsEverywhere = await roleAssignments.PermissionsOfAsync(principalId, cancellationToken).ConfigureAwait(false);
        var theirsAnywhere = await roleAssignments.PermissionsOfAsync(principalId, AccessScope.Anywhere, cancellationToken).ConfigureAwait(false);
        var mineEverywhere = await roleAssignments.PermissionsOfAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        var mineAnywhere = await roleAssignments.PermissionsOfAsync(actor.Id, AccessScope.Anywhere, cancellationToken).ConfigureAwait(false);
        return [.. theirsEverywhere.Except(mineEverywhere, StringComparer.Ordinal).Union(theirsAnywhere.Except(mineAnywhere, StringComparer.Ordinal), StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>The actor's display name, as the person's security activity names them.</summary>
    private async Task<string?> ActorNameAsync(CancellationToken cancellationToken) =>
        await context.Principals.AsNoTracking().Where(p => p.Id == actor.Id).Select(p => p.DisplayName).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public async Task<Result<NewUser>> CreateAsync(string loginId, string displayName, Guid homeUnitId, CancellationToken cancellationToken)
    {
        var normalised = Principal.NormaliseLoginId(loginId);
        var unit = await units.FindAsync(homeUnitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        {
            return Error.Validation(IdentityErrors.HomeUnitNotFound, "There is no organisation unit with this id.", new FieldError("home_unit_id", "not_found", "Choose an existing unit."));
        }

        if (unit.Status != UnitStatus.Active)
        {
            return Error.Validation(IdentityErrors.HomeUnitInactive, $"The unit '{unit.Name}' is deactivated; nobody can be created in it.", new FieldError("home_unit_id", "inactive", "Choose an active unit."));
        }

        if (await context.Principals.AnyAsync(p => p.LoginId == normalised, cancellationToken).ConfigureAwait(false))
        {
            return new Error(ErrorKind.Conflict, IdentityErrors.LoginIdTaken, $"Someone already signs in as '{normalised}'.", [new FieldError("login_id", "taken", "Another account has this login id. Choose another.")]);
        }

        // A removed person's identifier stays theirs (TF-219, ADR-0051): the tombstone outlives the principal.
        if (await context.LoginIdReservations.AnyAsync(r => r.LoginId == normalised, cancellationToken).ConfigureAwait(false))
        {
            return new Error(ErrorKind.Conflict, IdentityErrors.LoginIdRetired, $"'{normalised}' belonged to someone before, and a login identifier is never given to a second person.", [new FieldError("login_id", "retired", "This login id belonged to someone before. Choose another.")]);
        }

        var principal = new Principal(normalised, PrincipalType.User, displayName, normalised.Contains('@', StringComparison.Ordinal) ? normalised : null);
        principal.SetHomeUnit(unit.Id);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.Principals.Add(principal);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var token = await invitations.InviteAsync(principal, cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["display_name"] = principal.DisplayName, ["home_unit_id"] = unit.Id.ToString() };
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.UserCreated, AuditOutcome.Success, actor.Id, principal.LoginId, details, RecordType, principal.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new NewUser(principal, token, principal.InvitationExpiresAt!.Value);
    }
}
