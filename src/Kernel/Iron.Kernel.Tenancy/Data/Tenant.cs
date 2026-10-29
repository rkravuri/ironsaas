using System.Text.RegularExpressions;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Tenancy.Contracts;

namespace Iron.Kernel.Tenancy.Data;

/// <summary>A tenant of the platform (TF-001). The slug is the identifier used everywhere (ADR-0007); the row id is the key.</summary>
public sealed class Tenant : AuditedEntity, ITenantWide
{
    public const int DisplayNameMaxLength = 200;

    private Tenant()
    {
        Slug = string.Empty;
        DisplayName = string.Empty;
        Region = string.Empty;
        Plan = string.Empty;
        AdministratorLoginId = string.Empty;
        DatabaseName = string.Empty;
        StorageContainer = string.Empty;
    }

    public Tenant(TenantId tenant, string displayName, string region, string plan, SizeClass sizeClass, string administratorLoginId, string? administratorDisplayName)
    {
        if (tenant.IsEmpty)
        {
            throw new ArgumentException("The tenant identifier is empty.", nameof(tenant));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorLoginId);
        Slug = tenant.Value;
        DisplayName = displayName.Trim();
        Region = region.Trim();
        Plan = plan.Trim();
        SizeClass = sizeClass;
        Status = TenantStatus.Provisioning;
        AdministratorLoginId = administratorLoginId.Trim().ToLowerInvariant();
        AdministratorDisplayName = administratorDisplayName?.Trim();
        DatabaseName = Slug;
        StorageContainer = Slug;
    }

    public string Slug { get; private set; }

    public string DisplayName { get; private set; }

    public string Region { get; private set; }

    public string Plan { get; private set; }

    public SizeClass SizeClass { get; private set; }

    public TenantStatus Status { get; private set; }

    public string AdministratorLoginId { get; private set; }

    public string? AdministratorDisplayName { get; private set; }

    /// <summary>The tenant's database; the unit's owner and app roles derive from it (T14).</summary>
    public string DatabaseName { get; private set; }

    public string StorageContainer { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    /// <summary>The grace days of the plan applied last (TF-040 AC3).</summary>
    public int GraceDays { get; private set; }

    /// <summary>Grows by one with every committed change of the tenant's entitlements; the store's copy carries it.</summary>
    public int EntitlementsRevision { get; private set; }

    /// <summary>The revision the store holds; behind <see cref="EntitlementsRevision"/> until the fleet reconciles (ADR-0034).</summary>
    public int PublishedRevision { get; private set; }

    /// <summary>
    /// The operator marked the tenant for testing (ADR-0057): its unit runs the sample module, whose records the smoke and
    /// the browser tests use. The fleet writes it into the unit secret at the next provisioning or update.
    /// </summary>
    public bool SampleRecords { get; private set; }

    public void MarkForSampleRecords(bool enabled) => SampleRecords = enabled;

    /// <summary>A plan applied: its code and grace days, and the next revision, which the caller writes into the history.</summary>
    public int ApplyPlan(string plan, int graceDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(graceDays);
        Plan = plan;
        GraceDays = graceDays;
        EntitlementsRevision++;
        return EntitlementsRevision;
    }

    /// <summary>The store took this revision. An older one written late never moves the mark back.</summary>
    public void Published(int revision) => PublishedRevision = Math.Max(PublishedRevision, revision);

    public void SetStatus(TenantStatus status, DateTimeOffset now)
    {
        Status = status;
        if (status == TenantStatus.Active)
        {
            ActivatedAt ??= now;
        }
    }
}

/// <summary>One hostname of a tenant (TF-001): unique across the platform, lowercase, without a port.</summary>
public sealed partial class TenantHostname : AuditedEntity
{
    public const int MaxLength = 253;

    private TenantHostname()
    {
        Hostname = string.Empty;
    }

    public TenantHostname(Guid tenantId, string hostname)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("The tenant id is empty.", nameof(tenantId));
        }

        var normalised = Normalise(hostname);
        if (!IsValid(normalised))
        {
            throw new ArgumentException($"'{hostname}' is not a valid hostname.", nameof(hostname));
        }

        TenantId = tenantId;
        Hostname = normalised;
    }

    /// <summary>The row id of the tenant (not the slug).</summary>
    public Guid TenantId { get; private set; }

    public string Hostname { get; private set; }

    public static string Normalise(string? hostname) => TenantContext.NormaliseHost(hostname);

    public static bool IsValid(string? hostname) => hostname is not null && hostname.Length is > 0 and <= MaxLength && HostnamePattern().IsMatch(hostname);

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$", RegexOptions.CultureInvariant)]
    private static partial Regex HostnamePattern();
}
