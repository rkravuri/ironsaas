using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Tenancy.Data;

/// <summary>What an entitlement is about: a licensed module, or a feature.</summary>
public enum EntitlementKind
{
    Module,
    Feature,
}

/// <summary>
/// One module or feature a tenant has (TF-040): its status (Enabled, Disabled, ReadOnly), how it is sold and its seats
/// for a module, and the subscription's dates. Expired is never stored: the unit works it out from the end date.
/// </summary>
public sealed class TenantEntitlement : AuditedEntity, ITenantWide
{
    private TenantEntitlement()
    {
        Code = string.Empty;
    }

    public TenantEntitlement(Guid tenantId, EntitlementKind kind, string code, EntitlementValue value)
        : this()
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("The tenant id is empty.", nameof(tenantId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        TenantId = tenantId;
        Kind = kind;
        Code = code;
        Set(value);
    }

    /// <summary>The row id of the tenant (not the slug).</summary>
    public Guid TenantId { get; private set; }

    public EntitlementKind Kind { get; private set; }

    public string Code { get; private set; }

    public EntitlementStatus Status { get; private set; }

    /// <summary>For a module: how it is sold. Null for a feature.</summary>
    public SubscriptionType? SubscriptionType { get; private set; }

    /// <summary>For a per-person module: the seats. Null otherwise.</summary>
    public int? UserLimit { get; private set; }

    public DateOnly StartsOn { get; private set; }

    public DateOnly? EndsOn { get; private set; }

    public EntitlementValue Value => new(Status, SubscriptionType, UserLimit, StartsOn, EndsOn);

    public void Set(EntitlementValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Status = value.Status;
        SubscriptionType = value.SubscriptionType;
        UserLimit = value.UserLimit;
        StartsOn = value.StartsOn;
        EndsOn = value.EndsOn;
    }
}

/// <summary>The values of an entitlement, as the preview, the history and the apply compare them.</summary>
public sealed record EntitlementValue(EntitlementStatus Status, SubscriptionType? SubscriptionType, int? UserLimit, DateOnly StartsOn, DateOnly? EndsOn);
