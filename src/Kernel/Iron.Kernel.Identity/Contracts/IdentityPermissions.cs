namespace Iron.Kernel.Identity.Contracts;

/// <summary>The permissions of the Identity module (TF-030).</summary>
public static class IdentityPermissions
{
    public static readonly Permission UsersRead = new("identity.users.read", "List and view users and service accounts", Risk.Low);

    public static readonly Permission UsersManage = new("identity.users.manage", "Invite, change, deactivate and reactivate users, unlock their login and issue their recovery links", Risk.High);

    /// <summary>
    /// Connecting, changing and removing single sign-on providers (TF-014, ADR-0048): a provider decides who may sign in as
    /// whom and with which roles, so it is critical, and every change asks for a recent authentication (TF-248).
    /// </summary>
    public static readonly Permission SsoManage = new("identity.sso.manage", "Connect, change, test and remove single sign-on providers", Risk.Critical);

    /// <summary>
    /// The sessions console (TF-022, TF-029, ADR-0049): see everyone's sessions and who is signed in now, end any session
    /// with a reason, and revoke every session and token of the tenant after an incident, each behind a recent
    /// authentication (TF-248).
    /// </summary>
    public static readonly Permission SessionsManage = new("identity.sessions.manage", "See and end anyone's sessions, and revoke every session and token of the tenant", Risk.High);

    /// <summary>
    /// Signing in as another person of the tenant (TF-025, ADR-0049): critical, since the impersonator acts with the
    /// person's permissions; always with a reason and a recent authentication, and only for someone whose permissions the
    /// impersonator holds.
    /// </summary>
    public static readonly Permission ImpersonationStart = new("identity.impersonation.start", "Sign in as another person of the tenant, with a reason, for a limited time", Risk.Critical);

    public static IReadOnlyList<Permission> All => [UsersRead, UsersManage, SsoManage, SessionsManage, ImpersonationStart];
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

    // Second factors (TF-013, ADR-0047).

    /// <summary>The sign-in under way is unknown, used or past its time (five minutes, an enrolment fifteen): sign in again from the password.</summary>
    public const string SignInExpired = "identity.sign_in.expired";

    /// <summary>A second factor that did not prove the person: a wrong or used code, a recovery code that does not work, a passkey that did not verify.</summary>
    public const string SecondFactorInvalid = "identity.second_factor.invalid";

    /// <summary>Three wrong codes in a row: no code is checked until the cooling period ends (TF-013 AC2).</summary>
    public const string SecondFactorCooling = "identity.second_factor.cooling";

    /// <summary>
    /// The tenant's policy now requires a second factor of the signed-in person, who has none: every request but the
    /// enrolment's own answers it, 403, until they enrol in their session, which goes on (ADR-0047).
    /// </summary>
    public const string MfaEnrolmentRequired = "identity.mfa.enrolment_required";

    /// <summary>The tenant does not allow signing in with a passkey alone.</summary>
    public const string PasskeySignInOff = "identity.passkey_sign_in.off";

    /// <summary>The tenant's policy does not allow this kind of factor to be enrolled.</summary>
    public const string FactorNotAllowed = "identity.factor.not_allowed";

    /// <summary>An enrolment that did not prove itself: the first code does not match, or the passkey did not register.</summary>
    public const string FactorInvalid = "identity.factor.invalid";

    /// <summary>A person has one authenticator app at a time; remove it to enrol another.</summary>
    public const string FactorAppExists = "identity.factor.app_exists";

    /// <summary>No factor of the person's with this id.</summary>
    public const string FactorNotFound = "identity.factor.not_found";

    /// <summary>The person must have a second factor, and this is their last one.</summary>
    public const string FactorLast = "identity.factor.last";

    /// <summary>The person has no factor, so there are no recovery codes to make.</summary>
    public const string FactorNone = "identity.factor.none";

    /// <summary>An administrator asked to reset the factors of someone holding permissions they do not hold (ADR-0046's rule).</summary>
    public const string FactorResetEscalation = "identity.factor_reset.escalation";

    /// <summary>The password or the second factor given to confirm it is the person did not prove them (TF-248).</summary>
    public const string ReauthenticationInvalid = "identity.reauthentication.invalid";

    /// <summary>A bearer token cannot re-authenticate: its client asks for a new token with its credentials (TF-248).</summary>
    public const string ReauthenticationToken = "identity.reauthentication.token";

    // Single sign-on (TF-014, ADR-0048).
    /// <summary>A password sign-in of a person who signs in only through a provider (TF-014 AC2): "Use your company login".</summary>
    public const string UseCompanyLogin = "identity.login.use_company_login";

    /// <summary>No provider with this id.</summary>
    public const string SsoProviderNotFound = "identity.sso.provider_not_found";

    /// <summary>Another provider has this name; the sign-in page could not tell them apart.</summary>
    public const string SsoNameTaken = "identity.sso.name_taken";

    /// <summary>A provider's settings that cannot work: a discovery address that is not HTTPS, a domain that is not one, and the like.</summary>
    public const string SsoInvalid = "identity.sso.invalid";

    /// <summary>A default role or a group mapping names a role its author may not grant at its scope (ADR-0031 rule 1, ADR-0048).</summary>
    public const string SsoEscalation = "identity.sso.escalation";

    /// <summary>The provider could not be reached, or its discovery document or keys could not be read.</summary>
    public const string SsoUnavailable = "identity.sso.unavailable";

    /// <summary>The provider's answer did not prove the sign-in: state, code, ID token or nonce. Nothing is said about which.</summary>
    public const string SsoFailed = "identity.sso.failed";

    /// <summary>The provider did not mark the person's email verified, so it matches and creates nobody (ADR-0048).</summary>
    public const string SsoEmailUnverified = "identity.sso.email_unverified";

    /// <summary>The provider's email is outside the provider's allowed domains (TF-014 AC3).</summary>
    public const string SsoDomainNotAllowed = "identity.sso.domain_not_allowed";

    /// <summary>The person is deactivated: single sign-on never re-creates or reactivates them (ADR-0048).</summary>
    public const string SsoDeactivated = "identity.sso.deactivated";

    /// <summary>The provider signed in someone with no account here, and it creates nobody.</summary>
    public const string SsoNoAccount = "identity.sso.no_account";

    /// <summary>
    /// A re-authentication through a provider whose ID token does not show a sign-in within the step-up window: the
    /// provider did not ask for a new one, so nothing is confirmed and the action stays refused (TF-248 AC6, ADR-0048).
    /// </summary>
    public const string SsoReauthenticationStale = "identity.sso.reauthentication_stale";

    /// <summary>The provider publishes no end-session endpoint, so there is nothing to sign out of there.</summary>
    public const string SsoNoEndSession = "identity.sso.no_end_session";

    /// <summary>A recovery link for a person who signs in only through a provider: it would not help (TF-014 AC2).</summary>
    public const string RecoverySsoOnly = "identity.recovery.sso_only";

    /// <summary>Marking a person single sign-on only when no provider could sign them in: they would be locked out.</summary>
    public const string UserSsoUnreachable = "identity.user.sso_unreachable";

    // Sessions and revocation (TF-021, TF-022, TF-029, ADR-0049).

    /// <summary>No session with this id, or none of the asker's.</summary>
    public const string SessionNotFound = "identity.session.not_found";

    /// <summary>The session has ended already.</summary>
    public const string SessionEnded = "identity.session.ended";

    /// <summary>The session asked to end is the one the request comes from: signing out ends it.</summary>
    public const string SessionCurrent = "identity.session.current";

    /// <summary>The typed confirmation of a tenant-wide revocation is not the tenant's identifier.</summary>
    public const string RevocationConfirmation = "identity.revocation.confirmation";

    // Impersonation, operator links and break-glass accounts (TF-025, TF-026, ADR-0049).

    /// <summary>A bearer token asked to sign in as someone: only a person signed in to the shell does.</summary>
    public const string ImpersonationToken = "identity.impersonation.token";

    /// <summary>No reason was given (TF-025 AC1).</summary>
    public const string ImpersonationReasonRequired = "identity.impersonation.reason_required";

    public const string ImpersonationSelf = "identity.impersonation.self";

    /// <summary>The person cannot be signed in as: not active, a service account, or a break-glass account.</summary>
    public const string ImpersonationNotAllowed = "identity.impersonation.not_allowed";

    /// <summary>The person holds a permission the impersonator does not (ADR-0046's rule, TF-025 AC4).</summary>
    public const string ImpersonationEscalation = "identity.impersonation.escalation";

    /// <summary>More minutes than the tenant allows, or fewer than one.</summary>
    public const string ImpersonationDuration = "identity.impersonation.duration";

    /// <summary>The tenant does not let the platform's operator sign in as its people.</summary>
    public const string ImpersonationOperatorRefused = "identity.impersonation.operator_refused";

    /// <summary>Ending an impersonation in a session that is not one.</summary>
    public const string ImpersonationNone = "identity.impersonation.none";

    /// <summary>A one-time sign-in link that does not work: used, expired, or never issued.</summary>
    public const string SignInLinkInvalid = "identity.sign_in_link.invalid";

    /// <summary>A break-glass command without what it needs, or for an account that is not one.</summary>
    public const string BreakGlassInvalid = "identity.break_glass.invalid";

    /// <summary>The operator who approves break-glass is the one who asked (TF-026 AC4).</summary>
    public const string BreakGlassSameOperator = "identity.break_glass.same_operator";

    /// <summary>A document the person must accept has a version they have not accepted: every request but the terms' own answers it (ADR-0049).</summary>
    public const string TermsAcceptanceRequired = "identity.terms.acceptance_required";
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

    /// <summary>A person confirmed it is them in their session, for a high-risk action (TF-248).</summary>
    public const string Reauthenticated = "identity.reauthenticated";

    /// <summary>A person renamed one of their factors.</summary>
    public const string FactorRenamed = "identity.factor_renamed";

    /// <summary>An administrator connected a single sign-on provider (ADR-0048); the details never hold its client secret.</summary>
    public const string SsoProviderCreated = "identity.sso_provider_created";

    /// <summary>An administrator changed a provider: the fields that changed, and whether the client secret was replaced.</summary>
    public const string SsoProviderChanged = "identity.sso_provider_changed";

    /// <summary>An administrator removed a provider, its links and the roles its mapping gave.</summary>
    public const string SsoProviderDeleted = "identity.sso_provider_deleted";

    /// <summary>
    /// A session was ended before its time (TF-022 AC1): by an administrator with a reason, by its person from another
    /// session, or by a newer sign-in past the tenant's limit (TF-021 AC2). The reason and who ended it are in the event.
    /// </summary>
    public const string SessionEnded = "identity.session_ended";

    /// <summary>An administrator revoked every session and token of the tenant, with a reason (TF-029 AC1).</summary>
    public const string TenantRevoked = "identity.tenant_revoked";

    /// <summary>The operator was given a one-time link to sign in as a person, with a reason, through the control plane (TF-025).</summary>
    public const string ImpersonationLinkIssued = "identity.impersonation_link_issued";

    /// <summary>A break-glass account was opened: both operators, the ticket, the reason and the time limit (TF-026 AC1).</summary>
    public const string BreakGlassOpened = "identity.break_glass_opened";

    /// <summary>A break-glass account was closed, with the count of its actions; the review has the list (TF-026 AC3).</summary>
    public const string BreakGlassClosed = "identity.break_glass_closed";
}

/// <summary>
/// The kinds of a person's security events (TF-019, ADR-0046), shown in My profile under "Security activity". Each is also
/// written as the audit event of the same name with the <c>identity.</c> prefix. Second factors add theirs (ADR-0047);
/// impersonation adds its own later.
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

    /// <summary>A second factor was enrolled: an authenticator app or a passkey (TF-013).</summary>
    public const string FactorEnrolled = "factor_enrolled";

    /// <summary>The person removed one of their factors.</summary>
    public const string FactorRemoved = "factor_removed";

    /// <summary>Every factor and recovery code of the person was removed: by an administrator or the operator, with a reason (TF-013 AC3).</summary>
    public const string FactorsReset = "factors_reset";

    /// <summary>A recovery code signed the person in, or confirmed it was them; it never works again (TF-013 AC3).</summary>
    public const string RecoveryCodeUsed = "recovery_code_used";

    /// <summary>The person made a new set of recovery codes; the old ones stopped working.</summary>
    public const string RecoveryCodesRegenerated = "recovery_codes_regenerated";

    /// <summary>The person was linked to a single sign-on provider at their first sign-in through it (ADR-0048).</summary>
    public const string SsoLinked = "sso_linked";

    /// <summary>An administrator marked the person to sign in only through a provider (TF-014 AC2).</summary>
    public const string SsoOnlySet = "sso_only_set";

    /// <summary>An administrator let the person sign in with a password again.</summary>
    public const string SsoOnlyCleared = "sso_only_cleared";

    /// <summary>An administrator ended one of the person's sessions, with a reason (TF-022 AC1).</summary>
    public const string SessionEnded = "session_ended";

    /// <summary>An administrator revoked every session and token of the principal, with a reason (TF-029).</summary>
    public const string AccessRevoked = "access_revoked";

    /// <summary>Someone signed in as the person: who, until when, why (TF-025 AC6).</summary>
    public const string ImpersonationStarted = "impersonation_started";

    /// <summary>The impersonation ended: at its time, or ended by the impersonator.</summary>
    public const string ImpersonationEnded = "impersonation_ended";

    public static IReadOnlyList<string> All =>
    [
        PasswordChanged, RecoveryLinkIssued, RecoveryLinkUsed, PasswordSetByRecovery, LoginLocked, LoginUnlocked, UserDeactivated, UserReactivated,
        FactorEnrolled, FactorRemoved, FactorsReset, RecoveryCodeUsed, RecoveryCodesRegenerated,
        SsoLinked, SsoOnlySet, SsoOnlyCleared,
        SessionEnded, AccessRevoked, ImpersonationStarted, ImpersonationEnded,
    ];

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
