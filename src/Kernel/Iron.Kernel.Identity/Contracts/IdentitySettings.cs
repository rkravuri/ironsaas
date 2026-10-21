namespace Iron.Kernel.Identity.Contracts;

/// <summary>
/// Identity's settings (TF-021, TF-018, ADR-0036). The session limits are the tenant's: a new session takes them, and
/// <c>/me</c> answers a session's own limits, so the shell's warning follows. The configuration keys
/// <c>Identity:Session:IdleMinutes</c> and <c>AbsoluteHours</c> remain only as the system default, used while a tenant
/// has set nothing. An invitation takes the validity of the unit it is sent into, walking up to the tenant; by default
/// three days, TF-018's 72 hours.
/// </summary>
public static class IdentitySettings
{
    public static readonly Setting<int> SessionIdleMinutes = Setting.WholeNumber(
        "identity.session.idle_minutes", "How long a session stays open without activity before it ends, in minutes. A new session takes the value; open sessions keep theirs.", 60, 15, 480, SettingScopes.Tenant, visibleToShell: true);

    public static readonly Setting<int> SessionAbsoluteHours = Setting.WholeNumber(
        "identity.session.absolute_hours", "How long a session may last at most, active or not, in hours. A new session takes the value; open sessions keep theirs.", 12, 1, 24, SettingScopes.Tenant, visibleToShell: true);

    public static readonly Setting<int> InvitationValidDays = Setting.WholeNumber(
        "identity.invitation.valid_days", "How long an invitation link stays valid, in days. An invitation takes the value of the unit it is sent into.", 3, 1, 30, SettingScopes.Tenant | SettingScopes.Unit);

    // The password policy (TF-012, ADR-0046): length and a common-password check rather than composition rules, as NIST
    // SP 800-63B advises. Every path that sets a password applies it: an invitation, a recovery link, a change.

    public static readonly Setting<int> PasswordMinimumLength = Setting.WholeNumber(
        "identity.password.minimum_length", "The fewest characters a new password may have.", 12, 8, 64, SettingScopes.Tenant);

    public static readonly Setting<bool> PasswordCommonCheck = Setting.Boolean(
        "identity.password.common_check", "Refuse a new password that is on the list of the most common passwords. Always on.", true, SettingScopes.Tenant,
        rule: SettingRule.Must(value => value.ValueKind == System.Text.Json.JsonValueKind.True, "Always on: a common password is refused for every tenant."));

    public static readonly Setting<int> PasswordHistoryDepth = Setting.WholeNumber(
        "identity.password.history_depth", "How many of a person's previous passwords may not be used again; 0 allows any.", 10, 0, 24, SettingScopes.Tenant);

    public static readonly Setting<int> PasswordMaximumAgeDays = Setting.WholeNumber(
        "identity.password.maximum_age_days", "After how many days a password must be changed, with a warning 14 days before; 0 is off.", 0,
        SettingRule.Must(value => value.TryGetInt32(out var days) && (days == 0 || days is >= 30 and <= 365), "Off (0), or a whole number of days from 30 to 365."),
        SettingScopes.Tenant);

    public static readonly Setting<int> LockoutThreshold = Setting.WholeNumber(
        "identity.lockout.threshold", "How many failed sign-ins within the window lock a login identifier.", 5, 3, 10, SettingScopes.Tenant);

    public static readonly Setting<int> LockoutWindowMinutes = Setting.WholeNumber(
        "identity.lockout.window_minutes", "The window in which failed sign-ins are counted, in minutes.", 15, 5, 60, SettingScopes.Tenant);

    public static readonly Setting<int> LockoutDurationMinutes = Setting.WholeNumber(
        "identity.lockout.duration_minutes", "How long a locked login identifier stays locked, in minutes.", 15, 5, 120, SettingScopes.Tenant);

    public static IReadOnlyList<SettingDefinition> All =>
    [
        SessionIdleMinutes, SessionAbsoluteHours, InvitationValidDays,
        PasswordMinimumLength, PasswordCommonCheck, PasswordHistoryDepth, PasswordMaximumAgeDays,
        LockoutThreshold, LockoutWindowMinutes, LockoutDurationMinutes,
    ];
}
