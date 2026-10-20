namespace Iron.Kernel.Tenancy.Contracts;

/// <summary>Where a tenant stands (TF-001).</summary>
public enum TenantStatus
{
    Provisioning,
    Active,
    Suspended,
    ReadOnly,
    Decommissioned,
}

/// <summary>How big a unit is: the limits of its containers (TF-210, size classes in T14).</summary>
public enum SizeClass
{
    Small,
    Medium,
    Large,
}

/// <summary>A tenant as other modules see it. The fleet provisions from this (T14).</summary>
public sealed record TenantRecord(
    Guid Id,
    string Slug,
    string DisplayName,
    IReadOnlyList<string> Hostnames,
    string Region,
    string Plan,
    SizeClass SizeClass,
    TenantStatus Status,
    string AdministratorLoginId,
    string? AdministratorDisplayName,
    string DatabaseName,
    string StorageContainer,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt);

/// <summary>The registry (TF-001) as other modules use it: the fleet reads a tenant and moves its status.</summary>
public interface ITenantRegistry
{
    Task<TenantRecord?> FindAsync(string slug, CancellationToken cancellationToken);

    Task<Result> SetStatusAsync(string slug, TenantStatus status, CancellationToken cancellationToken);
}

public static class TenancyPermissions
{
    public static readonly Permission TenantsRead = new("tenancy.tenants.read", "List and view tenants", Risk.Low);

    public static readonly Permission TenantsManage = new("tenancy.tenants.manage", "Create and change tenants", Risk.High);

    public static readonly Permission TenantsProvision = new("tenancy.tenants.provision", "Request provisioning and de-provisioning of tenants", Risk.Critical);

    public static readonly Permission EntitlementsRead = new("tenancy.entitlements.read", "View plans, tenants' entitlements and their history", Risk.Low);

    public static readonly Permission EntitlementsManage = new("tenancy.entitlements.manage", "Define plans and apply them to tenants", Risk.High);

    public static IReadOnlyList<Permission> All => [TenantsRead, TenantsManage, TenantsProvision, EntitlementsRead, EntitlementsManage];
}

public static class TenancyErrors
{
    public const string TenantNotFound = "tenancy.tenant.not_found";
    public const string SlugTaken = "tenancy.tenant.slug_taken";
    public const string HostnameTaken = "tenancy.hostname.taken";
    public const string HostnameInvalid = "tenancy.hostname.invalid";
    public const string AlreadyActive = "tenancy.tenant.already_active";
    public const string NotProvisionable = "tenancy.tenant.not_provisionable";
    public const string PlanNotFound = "tenancy.plan.not_found";
    public const string PlanCodeInvalid = "tenancy.plan.code_invalid";
    public const string PlanUnknownEntitlements = "tenancy.plan.unknown_entitlements";
    public const string PlanDatesInvalid = "tenancy.plan.dates_invalid";
    public const string HistoryReadOnly = "tenancy.entitlement_history.read_only";
}

public static class TenancyAuditEvents
{
    public const string TenantCreated = "tenancy.tenant.created";
    public const string ProvisioningRequested = "tenancy.provisioning_requested";
    public const string DeprovisioningRequested = "tenancy.deprovisioning_requested";
    public const string UpdateRequested = "tenancy.update_requested";
    public const string StatusChanged = "tenancy.tenant.status_changed";
    public const string PlanSaved = "tenancy.plan_saved";
    public const string PlanApplied = "tenancy.plan_applied";
}

/// <summary>
/// A tenant's entitlements as the fleet uses them (ADR-0034): written to the store at <c>tenants/&lt;slug&gt;/entitlements</c>
/// again, which the secrets step does on every provisioning and update, so a store that lost them heals with the unit.
/// </summary>
public interface ITenantEntitlements
{
    /// <summary>Writes the tenant's effective entitlements to the store; false when the store could not take them (the fleet reconciles every minute).</summary>
    Task<bool> PublishAsync(string slug, CancellationToken cancellationToken);
}
