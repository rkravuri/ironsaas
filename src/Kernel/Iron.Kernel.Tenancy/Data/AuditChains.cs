using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Tenancy.Data;

/// <summary>
/// Which control-plane service account is a tenant's unit (ADR-0050): the fleet makes one per tenant, and every anchor it
/// sends is that tenant's, whatever a request might say.
/// </summary>
public sealed class UnitClient : AuditedEntity, ITenantWide
{
    private UnitClient()
    {
        TenantSlug = string.Empty;
    }

    public UnitClient(string tenantSlug, Guid principalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantSlug);
        TenantSlug = tenantSlug;
        PrincipalId = principalId;
    }

    public string TenantSlug { get; private set; }

    public Guid PrincipalId { get; private set; }

    public void Rebind(Guid principalId) => PrincipalId = principalId;
}

/// <summary>
/// One anchor of a tenant's audit chain, outside the tenant's database (TF-061, ADR-0050): the position the unit's chain
/// reached and its hash, or how far its oldest end was pruned. Written once; the app role may only insert and read.
/// </summary>
public sealed class AuditAnchorRecord : AppendOnlyEntity, ITenantWide
{
    public const int KindMaxLength = 20;
    public const int HashLength = 64;

    private AuditAnchorRecord()
    {
        TenantSlug = string.Empty;
        Kind = string.Empty;
        Hash = string.Empty;
    }

    public AuditAnchorRecord(string tenantSlug, string kind, long position, string hash, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        TenantSlug = tenantSlug;
        Kind = kind;
        Position = position;
        Hash = hash;
        At = at;
    }

    public string TenantSlug { get; private set; }

    /// <summary><c>seal</c> or <c>pruned</c> (Iron.Kernel.Audit.Contracts.AuditAnchorKinds).</summary>
    public string Kind { get; private set; }

    public long Position { get; private set; }

    public string Hash { get; private set; }

    /// <summary>When the unit sealed or pruned, by its clock; when the control plane took it is <see cref="AppendOnlyEntity.CreatedAt"/>.</summary>
    public DateTimeOffset At { get; private set; }
}

/// <summary>
/// One run of the fleet's verification of a tenant's chain (TF-061 AC1 to AC3): what it checked, its result, what it
/// found, and the restore point after a restore. Written once.
/// </summary>
public sealed class AuditVerificationRecord : AppendOnlyEntity, ITenantWide
{
    public const int ResultMaxLength = 20;

    private AuditVerificationRecord()
    {
        TenantSlug = string.Empty;
        Result = string.Empty;
        Problems = "[]";
    }

    public AuditVerificationRecord(
        string tenantSlug,
        Guid? jobId,
        bool full,
        long fromPosition,
        long toPosition,
        string? toHash,
        string result,
        int eventsChecked,
        string problems,
        long? restorePosition,
        DateTimeOffset? restoreAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        TenantSlug = tenantSlug;
        JobId = jobId;
        Full = full;
        FromPosition = fromPosition;
        ToPosition = toPosition;
        ToHash = toHash;
        Result = result;
        EventsChecked = eventsChecked;
        Problems = problems;
        RestorePosition = restorePosition;
        RestoreAt = restoreAt;
    }

    public string TenantSlug { get; private set; }

    public Guid? JobId { get; private set; }

    /// <summary>The whole chain, as once a month, rather than what was added since the last verification.</summary>
    public bool Full { get; private set; }

    public long FromPosition { get; private set; }

    public long ToPosition { get; private set; }

    /// <summary>The hash at <see cref="ToPosition"/>: where the next day's run picks up.</summary>
    public string? ToHash { get; private set; }

    public string Result { get; private set; }

    public int EventsChecked { get; private set; }

    /// <summary>The problems as JSON: kind, position, event id, message.</summary>
    public string Problems { get; private set; }

    public long? RestorePosition { get; private set; }

    public DateTimeOffset? RestoreAt { get; private set; }
}

/// <summary>An operator's request to verify a tenant's chain now (TF-061): the fleet's minute loop runs it and links the record.</summary>
public sealed class AuditVerificationRequest : AuditedEntity, ITenantWide
{
    private AuditVerificationRequest()
    {
        TenantSlug = string.Empty;
    }

    public AuditVerificationRequest(string tenantSlug, bool full, Guid requestedById)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantSlug);
        TenantSlug = tenantSlug;
        Full = full;
        RequestedById = requestedById;
    }

    public string TenantSlug { get; private set; }

    public bool Full { get; private set; }

    public Guid RequestedById { get; private set; }

    /// <summary>The verification that answered it; null while it waits.</summary>
    public Guid? VerificationId { get; private set; }

    public void Answer(Guid verificationId) => VerificationId = verificationId;
}
