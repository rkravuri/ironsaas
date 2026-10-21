using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>
/// One password a person had, as its Argon2id hash (TF-012, ADR-0046): a new password that verifies against one of the
/// last N is refused, N being the tenant's history depth. The current password is the newest entry. Only the newest
/// <see cref="Kept"/> stay, the most the setting allows, so a tenant that raises the depth finds them there; older
/// ones are deleted, because a hash kept longer is a risk with no use.
/// </summary>
public sealed class PasswordHistoryEntry : AuditedEntity
{
    /// <summary>The largest history depth a tenant may set.</summary>
    public const int Kept = 24;

    private PasswordHistoryEntry()
    {
        PasswordHash = string.Empty;
    }

    public PasswordHistoryEntry(Guid principalId, string passwordHash, DateTimeOffset setAt)
        : this()
    {
        if (principalId == Guid.Empty)
        {
            throw new ArgumentException("The principal id is empty.", nameof(principalId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PrincipalId = principalId;
        PasswordHash = passwordHash;
        SetAt = setAt;
    }

    public Guid PrincipalId { get; private set; }

    public string PasswordHash { get; private set; }

    /// <summary>When the password was set; the newest entries are compared.</summary>
    public DateTimeOffset SetAt { get; private set; }
}
