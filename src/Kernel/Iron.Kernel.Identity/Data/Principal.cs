using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>Who can authenticate (TF-010): a person, a service account, or an API key holder.</summary>
public enum PrincipalType
{
    User,
    ServiceAccount,
    ApiKey,
}

public enum PrincipalStatus
{
    Invited,
    Active,
    Locked,
    Disabled,
}

/// <summary>A principal of this tenant (TF-010). The login identifier is unique and stored in lowercase.</summary>
public sealed class Principal : AuditedEntity
{
    public const int LoginIdMaxLength = 254;
    public const int DisplayNameMaxLength = 200;

    private Principal()
    {
        LoginId = string.Empty;
        DisplayName = string.Empty;
    }

    public Principal(string loginId, PrincipalType type, string displayName, string? email)
    {
        LoginId = NormaliseLoginId(loginId);
        if (LoginId.Length == 0 || LoginId.Length > LoginIdMaxLength)
        {
            throw new ArgumentException("A login identifier is required (at most 254 characters).", nameof(loginId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Type = type;
        Status = PrincipalStatus.Invited;
        DisplayName = displayName.Trim();
        Email = email?.Trim();
    }

    public string LoginId { get; private set; }

    public PrincipalType Type { get; private set; }

    public PrincipalStatus Status { get; private set; }

    public string DisplayName { get; private set; }

    public string? Email { get; private set; }

    public string? Locale { get; private set; }

    public string? TimeZone { get; private set; }

    /// <summary>The Argon2id hash in PHC string form, or null while the principal has no local credential.</summary>
    public string? PasswordHash { get; private set; }

    public int FailedLoginAttempts { get; private set; }

    public DateTimeOffset? LastFailedLoginAt { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    /// <summary>SHA-256 of the invitation token while an invitation is open (TF-018).</summary>
    public string? InvitationTokenHash { get; private set; }

    public DateTimeOffset? InvitationExpiresAt { get; private set; }

    /// <summary>The organisation unit the principal belongs to (TF-034), an id in Authorization's tree; null until the seed or an administrator sets it.</summary>
    public Guid? HomeUnitId { get; private set; }

    public bool CanLogIn => Status == PrincipalStatus.Active && PasswordHash is not null;

    public static string NormaliseLoginId(string? loginId) => (loginId ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>Opens (or replaces) the invitation; the previous token stops working (TF-018 AC3).</summary>
    public void Invite(string tokenHash, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        InvitationTokenHash = tokenHash;
        InvitationExpiresAt = expiresAt;
        Status = PrincipalStatus.Invited;
    }

    public bool HasOpenInvitation(DateTimeOffset now) => InvitationTokenHash is not null && InvitationExpiresAt is not null && InvitationExpiresAt > now;

    /// <summary>The invitee set a password: the account becomes Active and the token is gone (TF-018 AC1).</summary>
    public void AcceptInvitation(string passwordHash, string? displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        InvitationTokenHash = null;
        InvitationExpiresAt = null;
        Status = PrincipalStatus.Active;
        FailedLoginAttempts = 0;
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            DisplayName = displayName.Trim();
        }
    }

    public void SetPassword(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public void RecordFailedLogin(DateTimeOffset now)
    {
        FailedLoginAttempts++;
        LastFailedLoginAt = now;
    }

    public void RecordLogin(DateTimeOffset now)
    {
        FailedLoginAttempts = 0;
        LastLoginAt = now;
    }

    public void Disable() => Status = PrincipalStatus.Disabled;

    public void SetHomeUnit(Guid unitId)
    {
        if (unitId == Guid.Empty)
        {
            throw new ArgumentException("The unit id is empty.", nameof(unitId));
        }

        HomeUnitId = unitId;
    }

    public void Activate() => Status = PrincipalStatus.Active;
}
