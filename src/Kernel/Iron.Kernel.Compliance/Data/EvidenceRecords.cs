using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Compliance.Data;

/// <summary>
/// An evidence pack for a period (TF-067, ADR-0052): who asked and when, where it stands, and once made, the ZIP in the
/// tenant's container with its size and hash, the hash of its manifest, and the audit chain's head the manifest names
/// with that position's anchor in the control plane.
/// </summary>
public sealed class EvidencePack : AuditedEntity, ITenantWide
{
    public const string RecordType = "evidence_pack";
    public const int FailureMaxLength = 1000;

    private EvidencePack()
    {
        RequesterName = string.Empty;
        Status = EvidencePackStates.Requested;
    }

    public EvidencePack(DateTimeOffset periodFrom, DateTimeOffset periodTo, Guid requestedById, string requesterName)
        : this()
    {
        PeriodFrom = periodFrom;
        PeriodTo = periodTo;
        RequestedById = requestedById;
        RequesterName = requesterName;
    }

    public DateTimeOffset PeriodFrom { get; private set; }

    public DateTimeOffset PeriodTo { get; private set; }

    public Guid RequestedById { get; private set; }

    public string RequesterName { get; private set; }

    public string Status { get; private set; }

    public string? BlobKey { get; private set; }

    public long? Bytes { get; private set; }

    /// <summary>The SHA-256 of the ZIP as stored, checked again on every download.</summary>
    public string? Sha256 { get; private set; }

    public string? ManifestSha256 { get; private set; }

    public int? ChainSegment { get; private set; }

    public long? ChainPosition { get; private set; }

    public string? ChainHash { get; private set; }

    public Guid? AnchorId { get; private set; }

    public DateTimeOffset? MadeAt { get; private set; }

    public string? Failure { get; private set; }

    public void Start() => Status = EvidencePackStates.Building;

    public void Made(string blobKey, long bytes, string sha256, string manifestSha256, EvidenceChainHead? head, DateTimeOffset at)
    {
        Status = EvidencePackStates.Ready;
        BlobKey = blobKey;
        Bytes = bytes;
        Sha256 = sha256;
        ManifestSha256 = manifestSha256;
        ChainSegment = head?.Segment;
        ChainPosition = head?.Position;
        ChainHash = head?.Hash;
        AnchorId = head?.Anchor?.Id;
        MadeAt = at;
        Failure = null;
    }

    public void Fail(string reason, DateTimeOffset at)
    {
        Status = EvidencePackStates.Failed;
        Failure = reason.Length > FailureMaxLength ? reason[..FailureMaxLength] : reason;
        MadeAt = at;
    }
}

/// <summary>
/// A short-lived link to download an evidence pack (TF-067 AC2, ADR-0052): for one person, for five minutes, once. Only
/// the token's hash is kept; the link carries the token.
/// </summary>
public sealed class EvidenceDownloadLink : AuditedEntity, ITenantWide
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private EvidenceDownloadLink()
    {
        TokenHash = string.Empty;
    }

    public EvidenceDownloadLink(Guid packId, Guid principalId, string tokenHash, DateTimeOffset expiresAt)
        : this()
    {
        PackId = packId;
        PrincipalId = principalId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
    }

    public Guid PackId { get; private set; }

    public Guid PrincipalId { get; private set; }

    public string TokenHash { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    public void Use(DateTimeOffset at) => UsedAt = at;
}
