namespace Iron.Kernel.Identity.Contracts;

/// <summary>The permissions of the Identity module (TF-030).</summary>
public static class IdentityPermissions
{
    public static readonly Permission UsersRead = new("identity.users.read", "List and view users and service accounts", Risk.Low);

    public static readonly Permission UsersManage = new("identity.users.manage", "Invite, change, deactivate and reactivate users, unlock their login and issue their recovery links", Risk.High);

    public static IReadOnlyList<Permission> All => [UsersRead, UsersManage];
}

/// <summary>Error codes of the Identity module.</summary>
public static class IdentityErrors
{
    /// <summary>One message for every failed login, so nothing reveals whether the account exists (TF-011 AC2).</summary>
    public const string LoginInvalid = "identity.login.invalid";
    public const string InvitationNotFound = "identity.invitation.not_found";
    public const string InvitationExpired = "identity.invitation.expired";
    // A password the tenant's policy refuses (TF-012, ADR-0046): one code per rule, and the field error's code is the rule.
    public const string PasswordTooShort = "identity.password.too_short";
    public const string PasswordTooLong = "identity.password.too_long";
    public const string PasswordIsLoginId = "identity.password.is_login_id";
    public const string PasswordCommon = "identity.password.common";
    public const string PasswordReused = "identity.password.reused";

    /// <summary>The current password given to change it is not the current password (TF-017).</summary>
    public const string PasswordCurrentWrong = "identity.password.current_wrong";

    /// <summary>The password is past the tenant's maximum age: the answer carries a recovery code to set a new one (TF-012).</summary>
    public const string PasswordExpired = "identity.password.expired";

    /// <summary>Too many failed sign-ins for this login identifier, known or not (TF-012, ADR-0046).</summary>
    public const string LoginLocked = "identity.login.locked";

    /// <summary>No recovery link with this code is open: never issued, used already, replaced by a newer one, or expired.</summary>
    public const string RecoveryInvalid = "identity.recovery.invalid";

    /// <summary>A recovery link for a person who cannot sign in: deactivated, a service account, or never activated.</summary>
    public const string RecoveryNotAllowed = "identity.recovery.not_allowed";

    /// <summary>An administrator asked for a recovery link for someone holding permissions they do not hold (ADR-0046).</summary>
    public const string RecoveryEscalation = "identity.recovery.escalation";

    /// <summary>The operator's recovery names someone who is not a tenant administrator of the whole tenant (ADR-0046).</summary>
    public const string RecoveryNotAdministrator = "identity.recovery.not_administrator";

    /// <summary>A change that does not apply to the person as they are: deactivating someone deactivated, and the like.</summary>
    public const string UserStatusConflict = "identity.user.status_conflict";

    /// <summary>Deactivating this person would leave the tenant without an active tenant administrator (A2's guard).</summary>
    public const string UserLastAdministrator = "identity.user.last_administrator";

    /// <summary>A person may not deactivate themselves: another administrator does it, so the tenant always keeps one.</summary>
    public const string UserSelf = "identity.user.self";
    public const string LoginIdTaken = "identity.login_id.taken";
    public const string ClientInvalid = "identity.client.invalid";

    /// <summary>No user with this id, or none the reader may see: the same answer, so a link reveals nothing (ADR-0031).</summary>
    public const string UserNotFound = "identity.user.not_found";
    public const string HomeUnitNotFound = "identity.home_unit.not_found";
    public const string HomeUnitInactive = "identity.home_unit.inactive";

    /// <summary>A bearer token asked to act for someone: only a person signed in to the shell acts for anyone (ADR-0037).</summary>
    public const string ActingToken = "identity.acting.token";

    /// <summary>No delegation from that person to the asker is active now.</summary>
    public const string ActingNoDelegation = "identity.acting.no_delegation";

    /// <summary>A directory search shorter than two characters: it would list the tenant's people one page at a time.</summary>
    public const string DirectorySearchTooShort = "identity.directory.search_too_short";

    /// <summary>A bearer token asked for a working unit: a token sees what its scopes cover and works for no unit (ADR-0038).</summary>
    public const string WorkingUnitToken = "identity.working_unit.token";

    /// <summary>A unit the person may not work for, or "All my units" for a person who may not choose it.</summary>
    public const string WorkingUnitNotYours = "identity.working_unit.not_yours";
}

/// <summary>Audit event types raised by Identity (TF-060).</summary>
public static class IdentityAuditEvents
{
    public const string LoginSucceeded = "identity.login_succeeded";
    public const string LoginFailed = "identity.login_failed";
    public const string Logout = "identity.logout";
    public const string SessionExpired = "identity.session_expired";
    public const string InvitationCreated = "identity.invitation_created";
    public const string InvitationAccepted = "identity.invitation_accepted";
    public const string TokenIssued = "identity.token_issued";
    public const string TokenRefused = "identity.token_refused";

    /// <summary>A person chose to act for a delegator (ADR-0037); the event names both.</summary>
    public const string ActingStarted = "identity.acting_started";

    /// <summary>A person stopped acting for a delegator.</summary>
    public const string ActingStopped = "identity.acting_stopped";

    /// <summary>The delegation behind a session's acting ended, and the next request cleared it.</summary>
    public const string ActingEnded = "identity.acting_ended";

    /// <summary>A person chose the unit they work for, or all their units (ADR-0038).</summary>
    public const string WorkingUnitChosen = "identity.working_unit_chosen";

    /// <summary>The seed was given a different secret for an existing service account and set it (ADR-0028: a healed unit).</summary>
    public const string ServiceAccountSecretChanged = "identity.service_account_secret_changed";

    /// <summary>A principal was given its home unit (TF-034): by the seed for the root, later by an administrator.</summary>
    public const string HomeUnitSet = "identity.home_unit_set";

    /// <summary>An administrator created a user in a unit and opened their invitation (A2).</summary>
    public const string UserCreated = "identity.user_created";

    /// <summary>A person changed their own password with the current one (TF-017).</summary>
    public const string PasswordChanged = "identity.password_changed";

    /// <summary>A recovery link was issued: by an administrator, by the operator with a reason, or at a sign-in with an expired password.</summary>
    public const string RecoveryLinkIssued = "identity.recovery_link_issued";

    /// <summary>A recovery link was used: it is consumed and every session of the person ended.</summary>
    public const string RecoveryLinkUsed = "identity.recovery_link_used";

    /// <summary>A password was set through a recovery link (TF-017).</summary>
    public const string PasswordSetByRecovery = "identity.password_set_by_recovery";

    /// <summary>A login identifier reached the failure threshold and is locked (TF-012); known or not.</summary>
    public const string LoginLocked = "identity.login_locked";

    /// <summary>An administrator unlocked a login identifier.</summary>
    public const string LoginUnlocked = "identity.login_unlocked";

    /// <summary>An administrator deactivated a person: they cannot sign in, their sessions ended, their tokens are refused.</summary>
    public const string UserDeactivated = "identity.user_deactivated";

    /// <summary>An administrator reactivated a person.</summary>
    public const string UserReactivated = "identity.user_reactivated";
}

/// <summary>
/// The kinds of a person's security events (TF-019, ADR-0046), shown in My profile under "Security activity". Each is also
/// written as the audit event of the same name with the <c>identity.</c> prefix. MFA and impersonation add theirs later.
/// </summary>
public static class SecurityEventKinds
{
    public const string PasswordChanged = "password_changed";
    public const string RecoveryLinkIssued = "recovery_link_issued";
    public const string RecoveryLinkUsed = "recovery_link_used";
    public const string PasswordSetByRecovery = "password_set_by_recovery";
    public const string LoginLocked = "login_locked";
    public const string LoginUnlocked = "login_unlocked";
    public const string UserDeactivated = "user_deactivated";
    public const string UserReactivated = "user_reactivated";

    public static IReadOnlyList<string> All => [PasswordChanged, RecoveryLinkIssued, RecoveryLinkUsed, PasswordSetByRecovery, LoginLocked, LoginUnlocked, UserDeactivated, UserReactivated];

    /// <summary>The audit event a security event is written as.</summary>
    public static string AuditEventOf(string kind) => $"identity.{kind}";
}

/// <summary>Who caused a security event, for the shell to say in words: the person, an administrator, the operator, or the system.</summary>
public static class SecurityEventActors
{
    public const string Self = "self";
    public const string Administrator = "administrator";
    public const string Operator = "operator";

    /// <summary>Nobody in particular: a lockout after failed sign-ins, a link issued because the password expired.</summary>
    public const string System = "system";
}
