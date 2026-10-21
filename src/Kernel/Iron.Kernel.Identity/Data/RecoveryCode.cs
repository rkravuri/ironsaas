using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>
/// One of a person's ten recovery codes (TF-013 AC3, ADR-0047), kept as the SHA-256 of the person's id and the code: the
/// code itself is shown once and never stored. A code works once; regenerating replaces all ten, and a reset of the
/// person's factors removes them.
/// </summary>
public sealed class RecoveryCode : AuditedEntity
{
    private RecoveryCode()
    {
        CodeHash = string.Empty;
    }

    public RecoveryCode(Guid principalId, string codeHash)
        : this()
    {
        if (principalId == Guid.Empty)
        {
            throw new ArgumentException("The principal id is empty.", nameof(principalId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        PrincipalId = principalId;
        CodeHash = codeHash;
    }

    public Guid PrincipalId { get; private set; }

    public string CodeHash { get; private set; }

    /// <summary>When the code was used; a used code never works again.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    public bool IsUsed => UsedAt is not null;

    public void Use(DateTimeOffset now) => UsedAt ??= now;
}
