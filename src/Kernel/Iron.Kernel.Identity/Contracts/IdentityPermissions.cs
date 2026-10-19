namespace Iron.Kernel.Identity.Contracts;

/// <summary>The permissions of the Identity module (TF-030).</summary>
public static class IdentityPermissions
{
    public static readonly Permission UsersRead = new("identity.users.read", "List and view users and service accounts", Risk.Low);

    public static readonly Permission UsersManage = new("identity.users.manage", "Invite, change, disable and re-enable users", Risk.High);

    public static IReadOnlyList<Permission> All => [UsersRead, UsersManage];
}

/// <summary>Error codes of the Identity module.</summary>
public static class IdentityErrors
{
    /// <summary>One message for every failed login, so nothing reveals whether the account exists (TF-011 AC2).</summary>
    public const string LoginInvalid = "identity.login.invalid";
    public const string InvitationNotFound = "identity.invitation.not_found";
    public const string InvitationExpired = "identity.invitation.expired";
    public const string PasswordTooShort = "identity.password.too_short";
    public const string PasswordIsLoginId = "identity.password.is_login_id";
    public const string LoginIdTaken = "identity.login_id.taken";
    public const string ClientInvalid = "identity.client.invalid";

    /// <summary>No user with this id, or none the reader may see: the same answer, so a link reveals nothing (ADR-0031).</summary>
    public const string UserNotFound = "identity.user.not_found";
    public const string HomeUnitNotFound = "identity.home_unit.not_found";
    public const string HomeUnitInactive = "identity.home_unit.inactive";
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

    /// <summary>The seed was given a different secret for an existing service account and set it (ADR-0028: a healed unit).</summary>
    public const string ServiceAccountSecretChanged = "identity.service_account_secret_changed";

    /// <summary>A principal was given its home unit (TF-034): by the seed for the root, later by an administrator.</summary>
    public const string HomeUnitSet = "identity.home_unit_set";

    /// <summary>An administrator created a user in a unit and opened their invitation (A2).</summary>
    public const string UserCreated = "identity.user_created";
}
