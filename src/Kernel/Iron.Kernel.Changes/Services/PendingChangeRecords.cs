using Iron.Kernel.Changes.Data;
using Iron.Kernel.Compliance.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Changes.Services;

/// <summary>
/// A pending change as a signable record (TF-063, ADR-0052): where the tenant asks, its approval is a signature meaning
/// "approved" and its rejection one meaning "rejected". The snapshot holds what was decided, the change's substance and
/// not its state, at the version of its review, so the signature still matches after the decision is recorded.
/// </summary>
public sealed class PendingChangeRecords(ChangesDbContext context, IServiceProvider services) : ISignableRecords
{
    public static SignableType Type { get; } = new(
        PendingChange.RecordType,
        "Configuration change",
        [SignatureMeanings.Approved, SignatureMeanings.Rejected],
        new SigningPolicy(false, [SignatureMeanings.Approved, SignatureMeanings.Rejected], true));

    public IReadOnlyList<SignableType> Types => [Type];

    public async Task<RecordSnapshot?> CurrentAsync(string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        if (!string.Equals(recordType, PendingChange.RecordType, StringComparison.Ordinal))
        {
            return null;
        }

        var change = await context.Changes.AsNoTracking().SingleOrDefaultAsync(c => c.Id == recordId, cancellationToken).ConfigureAwait(false);
        return change is null ? null : Snapshot(change);
    }

    public async Task<bool> MayReadAsync(string recordType, Guid recordId, CancellationToken cancellationToken) =>
        await services.GetRequiredService<PendingChanges>().FindAsync(recordId, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>What a signature on the change signs: its area, what it does and to what, before and after, who asked and why.</summary>
    public static RecordSnapshot Snapshot(PendingChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return RecordSnapshot.Of(PendingChange.RecordType, change.Id, change.Reviews, change.Title, new
        {
            change.Area,
            change.Kind,
            change.Subject,
            change.Title,
            Before = change.Before,
            After = change.After,
            RequestedBy = change.RequestedById,
            change.RequesterName,
            change.Reason,
            RequestedAt = change.CreatedAt,
        });
    }
}
