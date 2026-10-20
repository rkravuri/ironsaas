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

    public static IReadOnlyList<SettingDefinition> All => [SessionIdleMinutes, SessionAbsoluteHours, InvitationValidDays];
}
