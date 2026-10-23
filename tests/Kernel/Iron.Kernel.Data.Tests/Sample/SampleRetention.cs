using System.Text.Json;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Retention;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Data.Tests.Sample;

/// <summary>A business document of the sample module: an order from a supplier, kept for its class's period after it closed (TF-066).</summary>
public sealed class SampleOrder : BusinessRecord, ITrackedChanges
{
    public const string RecordType = "sample_order";

    private SampleOrder()
    {
    }

    public SampleOrder(Guid owningOrganisationId, Guid supplierId, string reference, DateTimeOffset closedAt)
        : base(owningOrganisationId)
    {
        SupplierId = supplierId;
        Reference = reference;
        ClosedAt = closedAt;
    }

    public Guid SupplierId { get; private set; }

    public string Reference { get; private set; } = string.Empty;

    public DateTimeOffset ClosedAt { get; private set; }

    /// <summary>Where the supplier is paid: its history keeps the last four characters only (TF-062 AC3).</summary>
    [Sensitive]
    public string? BankAccount { get; private set; }

    /// <summary>The supplier portal's key for this order, as its hash: its history says only that it changed.</summary>
    [Secret]
    public string? PortalKeyHash { get; private set; }

    /// <summary>When the supplier last opened the order in its portal: bookkeeping the history leaves out.</summary>
    [NotTracked]
    public DateTimeOffset? LastViewedAt { get; private set; }

    public void PayTo(string bankAccount) => BankAccount = bankAccount;

    public void ChangePortalKey(string hash) => PortalKeyHash = hash;

    public void Viewed(DateTimeOffset at) => LastViewedAt = at;
}

/// <summary>
/// The sample module's declaration (TF-066, ADR-0050): its orders are business documents, kept from their closure, and
/// belong to their supplier, so a legal hold on the supplier keeps them.
/// </summary>
public sealed class SampleOrderRetention(SampleDbContext context) : IRetainedRecords
{
    public const string Type = "sample_order";
    public const string SupplierType = "sample_supplier";

    public string RecordType => Type;

    public RetentionClass Class => RetentionClasses.BusinessDocument;

    public async Task<IReadOnlyList<RetainedRecord>> ExpiredAsync(DateTimeOffset cutoff, int max, CancellationToken cancellationToken)
    {
        var orders = await context.Orders.AsNoTracking().Where(o => o.ClosedAt < cutoff).OrderBy(o => o.ClosedAt).Take(max).ToListAsync(cancellationToken);
        return [.. orders.Select(o => new RetainedRecord(o.Id, o.ClosedAt, SupplierType, o.SupplierId, JsonSerializer.Serialize(new { id = o.Id, reference = o.Reference, supplier_id = o.SupplierId })))];
    }

    public async Task DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        await context.Orders.Where(o => ids.Contains(o.Id)).ExecuteDeleteAsync(cancellationToken);
}
