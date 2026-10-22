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
public sealed class Principal : AuditedEntity, IOwnAccessRule
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

    /// <summary>When the current password was set, which its maximum age counts from (TF-012); null without a password.</summary>
    public DateTimeOffset? PasswordChangedAt { get; private set; }

    /// <summary>SHA-256 of the code of an open recovery link (TF-017): one at a time, 15 minutes, consumed on use.</summary>
    public string? RecoveryTokenHash { get; private set; }

    public DateTimeOffset? RecoveryExpiresAt { get; private set; }

    /// <summary>Wrong second-factor codes in a row since the last right one (TF-013 AC2); a right one or a lock sets it to zero.</summary>
    public int SecondFactorFailures { get; private set; }

    /// <summary>Until when no code is checked, after every third wrong one in a row (TF-013 AC2).</summary>
    public DateTimeOffset? SecondFactorCoolingUntil { get; private set; }

    /// <summary>
    /// When a sign-in first found that the tenant's policy requires a factor of this person and they had none: the grace
    /// a tenant may give counts from here (ADR-0047). Cleared when the person enrols.
    /// </summary>
    public DateTimeOffset? MfaRequiredSince { get; private set; }

    /// <summary>
    /// Marked by an administrator to sign in only through a single sign-on provider (TF-014 AC2, ADR-0048): a password
    /// sign-in is refused with "Use your company login", and a recovery link does not apply. Everyone linked to a provider
    /// that is single sign-on only is held the same way without the mark.
    /// </summary>
    public bool SsoOnly { get; private set; }

    public bool CanLogIn => Status == PrincipalStatus.Active && PasswordHash is not null;

    /// <summary>A single sign-on provider may sign the principal in: a person who is active, or invited and never signed in (ADR-0048).</summary>
    public bool CanSignInThroughProvider => Type == PrincipalType.User && Status is PrincipalStatus.Active or PrincipalStatus.Invited;

    /// <summary>Every this many wrong codes in a row start a cooling period (TF-013 AC2).</summary>
    public const int CoolingEvery = 3;

    /// <summary>This many wrong codes in a row lock the login as too many wrong passwords do (TF-012, TF-013 AC2).</summary>
    public const int SecondFactorLockAfter = 10;

    public static readonly TimeSpan FirstCooling = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan LongestCooling = TimeSpan.FromMinutes(15);

    public bool IsCooling(DateTimeOffset now) => SecondFactorCoolingUntil is { } until && until > now;

    /// <summary>
    /// Counts a wrong second-factor code. Every third in a row starts a cooling period of 60 seconds, doubling with each
    /// further three, up to 15 minutes; the tenth in a row answers true, the caller locks the login, and the count starts
    /// again, as a lockout's does.
    /// </summary>
    public bool RecordSecondFactorFailure(DateTimeOffset now)
    {
        SecondFactorFailures++;
        if (SecondFactorFailures >= SecondFactorLockAfter)
        {
            SecondFactorFailures = 0;
            SecondFactorCoolingUntil = null;
            return true;
        }

        if (SecondFactorFailures % CoolingEvery == 0)
        {
            var doublings = (SecondFactorFailures / CoolingEvery) - 1;
            var cooling = FirstCooling * Math.Pow(2, doublings);
            SecondFactorCoolingUntil = now + (cooling < LongestCooling ? cooling : LongestCooling);
        }

        return false;
    }

    /// <summary>A right second factor: no failures counted, no cooling.</summary>
    public void RecordSecondFactorSuccess()
    {
        SecondFactorFailures = 0;
        SecondFactorCoolingUntil = null;
    }

    /// <summary>The policy requires a factor of the person, who has none: the grace counts from the first time this is found.</summary>
    public DateTimeOffset MfaRequiredFrom(DateTimeOffset now) => MfaRequiredSince ??= now;

    /// <summary>The person enrolled a factor, or no longer needs one.</summary>
    public void MfaSatisfied() => MfaRequiredSince = null;

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
    public void AcceptInvitation(string passwordHash, string? displayName, DateTimeOffset? passwordSetAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        PasswordChangedAt = passwordSetAt;
        InvitationTokenHash = null;
        InvitationExpiresAt = null;
        Status = PrincipalStatus.Active;
        FailedLoginAttempts = 0;
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            DisplayName = displayName.Trim();
        }
    }

    /// <summary>A new password, by a change or a recovery link (TF-017); its maximum age counts from now.</summary>
    public void SetPassword(string passwordHash, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        PasswordChangedAt = now ?? PasswordChangedAt;
    }

    /// <summary>Opens a recovery link, replacing any earlier one, which stops working (TF-017).</summary>
    public void OpenRecovery(string tokenHash, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        RecoveryTokenHash = tokenHash;
        RecoveryExpiresAt = expiresAt;
    }

    public bool HasOpenRecovery(DateTimeOffset now) => RecoveryTokenHash is not null && RecoveryExpiresAt is not null && RecoveryExpiresAt > now;

    /// <summary>The recovery link was used: it is consumed, whatever happens next (TF-017 AC1, AC2).</summary>
    public void ConsumeRecovery()
    {
        RecoveryTokenHash = null;
        RecoveryExpiresAt = null;
    }

    /// <summary>The password is past the tenant's maximum age at <paramref name="now"/>; never while the age is off (0).</summary>
    public bool PasswordExpired(DateTimeOffset now, int maximumAgeDays) =>
        maximumAgeDays > 0 && PasswordHash is not null && PasswordChangedAt is { } changed && now >= changed.AddDays(maximumAgeDays);

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

    public bool IsDeactivated => Status == PrincipalStatus.Disabled;

    /// <summary>
    /// Deactivated (TF-010 AC3): the person cannot sign in, and an open invitation or recovery link stops working with
    /// it, so neither can bring them back. Ending their sessions is the caller's, in the same transaction.
    /// </summary>
    public void Deactivate()
    {
        Status = PrincipalStatus.Disabled;
        InvitationTokenHash = null;
        InvitationExpiresAt = null;
        ConsumeRecovery();
    }

    /// <summary>
    /// A deactivated person comes back: Active with the password they had, or Invited when they never set one, and then
    /// the caller opens a new invitation, since the old one closed at deactivation.
    /// </summary>
    public void Reactivate() => Status = PasswordHash is null ? PrincipalStatus.Invited : PrincipalStatus.Active;

    /// <summary>Signs in only through a provider, or may use a password again (ADR-0048).</summary>
    public void MarkSsoOnly(bool ssoOnly) => SsoOnly = ssoOnly;

    /// <summary>
    /// A provider signed the person in (ADR-0048): one it created is active from the start, and an invited person whose
    /// verified email the provider asserted is active too, their invitation closed, since the provider proved the address
    /// the invitation went to. An active person stays as they are.
    /// </summary>
    public void ActivatedByProvider()
    {
        if (Status != PrincipalStatus.Invited)
        {
            return;
        }

        Status = PrincipalStatus.Active;
        InvitationTokenHash = null;
        InvitationExpiresAt = null;
    }

    public void SetHomeUnit(Guid unitId)
    {
        if (unitId == Guid.Empty)
        {
            throw new ArgumentException("The unit id is empty.", nameof(unitId));
        }

        HomeUnitId = unitId;
    }
}
