using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Tenancy.Data;

/// <summary>
/// One change of a tenant's entitlements (TF-045): the plan, the grace days, a module or a feature, with the values
/// before and after (JSON, null when it was added or removed), the revision it made, why, and who and when (the
/// created pair). Append-only: the app role may only insert and select, and the save interceptor refuses a change.
/// </summary>
public sealed class EntitlementChange : AppendOnlyEntity, ITenantWide
{
    public const string PlanKind = "plan";
    public const string GraceKind = "grace";
    public const int ReasonMaxLength = 500;

    private EntitlementChange()
    {
        TenantSlug = string.Empty;
        Kind = string.Empty;
        Code = string.Empty;
        Reason = string.Empty;
    }

    public EntitlementChange(Guid tenantId, string tenantSlug, int revision, string kind, string code, string? before, string? after, string reason)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        TenantId = tenantId;
        TenantSlug = tenantSlug;
        Revision = revision;
        Kind = kind;
        Code = code;
        Before = before;
        After = after;
        Reason = reason.Length > ReasonMaxLength ? reason[..ReasonMaxLength] : reason;
    }

    public Guid TenantId { get; private set; }

    public string TenantSlug { get; private set; }

    /// <summary>The revision of the tenant's entitlements this change made.</summary>
    public int Revision { get; private set; }

    /// <summary><c>plan</c>, <c>grace</c>, <c>module</c> or <c>feature</c>.</summary>
    public string Kind { get; private set; }

    public string Code { get; private set; }

    /// <summary>The values before (jsonb); null when the entitlement was added.</summary>
    public string? Before { get; private set; }

    /// <summary>The values after (jsonb); null when the entitlement was removed.</summary>
    public string? After { get; private set; }

    public string Reason { get; private set; }
}
