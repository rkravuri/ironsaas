using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Audit.Data;

/// <summary>
/// One pass of the sealer (TF-061, ADR-0050): the positions it gave, the hash it reached, and when. The latest is the
/// chain's head, and each is the anchor the unit sends to the control plane.
/// </summary>
public sealed class AuditSeal : AppendOnlyEntity, ITenantWide, IVerifierReadable
{
    private AuditSeal()
    {
        LastHash = string.Empty;
    }

    public AuditSeal(long firstPosition, long lastPosition, string lastHash)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstPosition, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastPosition, firstPosition);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastHash);
        FirstPosition = firstPosition;
        LastPosition = lastPosition;
        LastHash = lastHash;
    }

    public long FirstPosition { get; private set; }

    public long LastPosition { get; private set; }

    public string LastHash { get; private set; }
}
