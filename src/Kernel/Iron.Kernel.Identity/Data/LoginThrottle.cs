using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>
/// The failed sign-ins of one login identifier (TF-012, ADR-0046), kept for identifiers that exist and for those that do
/// not, so a lockout answers the same either way and reveals no account. Failures count within a window that opens at
/// the first one; reaching the threshold locks the identifier until <see cref="LockedUntil"/>, and while it is locked even
/// the correct password is refused. A lock lifts by itself, or an administrator unlocks it. It never touches the
/// principal's status or its open sessions: someone guessing at a login cannot sign its owner out.
/// </summary>
public sealed class LoginThrottle : AuditedEntity
{
    private LoginThrottle()
    {
        LoginId = string.Empty;
    }

    public LoginThrottle(string loginId)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginId);
        LoginId = loginId.Length > Principal.LoginIdMaxLength ? loginId[..Principal.LoginIdMaxLength] : loginId;
    }

    /// <summary>The identifier as it was typed, normalised; an identifier nobody has is kept the same way.</summary>
    public string LoginId { get; private set; }

    public int Failures { get; private set; }

    /// <summary>When the first failure of the current window happened; null after a success or an unlock.</summary>
    public DateTimeOffset? WindowStartedAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>
    /// Counts a failure. A failure after the window closed starts a new window. Answers true when this failure locked the
    /// identifier; failures while it is locked are not counted, so the lock does not grow by being tried.
    /// </summary>
    public bool RecordFailure(DateTimeOffset now, int threshold, TimeSpan window, TimeSpan duration)
    {
        if (IsLocked(now))
        {
            return false;
        }

        if (WindowStartedAt is not { } started || now - started >= window)
        {
            WindowStartedAt = now;
            Failures = 0;
        }

        Failures++;
        if (Failures < threshold)
        {
            return false;
        }

        LockedUntil = now + duration;
        Failures = 0;
        WindowStartedAt = null;
        return true;
    }

    /// <summary>Locks the identifier now, as reaching the threshold does: ten wrong second-factor codes in a row (TF-013 AC2).</summary>
    public void Lock(DateTimeOffset now, TimeSpan duration)
    {
        LockedUntil = now + duration;
        Failures = 0;
        WindowStartedAt = null;
    }

    /// <summary>A success or an administrator's unlock: no failures counted, no lock.</summary>
    public void Clear()
    {
        Failures = 0;
        WindowStartedAt = null;
        LockedUntil = null;
    }
}
