using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Tenancy.Data;

/// <summary>
/// A suite a tenant has (TF-244, ADR-0059): the plan it holds for the suite and that plan's grace days. The suite's
/// entitlements are the tenant's entitlements of the suite's group; the fleet installs the suite in the tenant's unit,
/// and removing the row switches the suite off while its data stays in the tenant's database.
/// </summary>
public sealed class TenantSuite : AuditedEntity, ITenantWide
{
    private TenantSuite()
    {
        Suite = string.Empty;
        Plan = string.Empty;
    }

    /// <summary>A suite the tenant gains; its plan comes with the first apply, in the same transaction.</summary>
    public TenantSuite(Guid tenantId, string suite)
        : this()
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("The tenant id is empty.", nameof(tenantId));
        }

        if (!SuiteName.IsShortName(suite))
        {
            throw new ArgumentException($"'{suite}' is not a suite's short name.", nameof(suite));
        }

        TenantId = tenantId;
        Suite = suite;
    }

    /// <summary>The row id of the tenant (not the slug).</summary>
    public Guid TenantId { get; private set; }

    /// <summary>The suite's short name: <c>procurement</c>.</summary>
    public string Suite { get; private set; }

    /// <summary>The code of the plan the tenant holds for the suite; empty until the first apply.</summary>
    public string Plan { get; private set; }

    /// <summary>The grace days of that plan (TF-040 AC3), which the suite's entitlements follow after their end date.</summary>
    public int GraceDays { get; private set; }

    public void ApplyPlan(string plan, int graceDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(graceDays);
        Plan = plan;
        GraceDays = graceDays;
    }
}
