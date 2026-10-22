using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>
/// An administrator revoked every session and token of the tenant (TF-029 AC1, ADR-0049), after an incident: the
/// latest one's time is the tenant's "valid after", before which no session started and no token issued is accepted.
/// Append-only, with its reason.
/// </summary>
public sealed class TenantRevocation : AppendOnlyEntity, ITenantWide
{
    public const int ReasonMaxLength = 500;

    private TenantRevocation()
    {
        Reason = string.Empty;
    }

    public TenantRevocation(DateTimeOffset revokedAt, string reason)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        RevokedAt = revokedAt;
        Reason = reason.Length > ReasonMaxLength ? reason[..ReasonMaxLength] : reason;
    }

    public DateTimeOffset RevokedAt { get; private set; }

    public string Reason { get; private set; }
}
