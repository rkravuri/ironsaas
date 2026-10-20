using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Authorization.Data;

/// <summary>
/// A person's duties handed to another person for a period, with a reason (TF-037, ADR-0037). The period is two instants;
/// ending early sets <see cref="EndedAt"/> and keeps the row, so the history of who could act for whom stays whole. The
/// created pair says who set it up: the delegator, or an administrator for them.
/// </summary>
public sealed class Delegation : AuditedEntity, IOwnAccessRule
{
    public const int ReasonMaxLength = 500;
    public const int DutyMaxLength = 100;

    private Delegation()
    {
        Duties = [];
        Reason = string.Empty;
    }

    public Delegation(Guid delegatorId, Guid delegateId, IEnumerable<string> duties, DateTimeOffset startsAt, DateTimeOffset endsAt, string reason)
        : this()
    {
        ArgumentNullException.ThrowIfNull(duties);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (endsAt <= startsAt)
        {
            throw new ArgumentException("A delegation ends after it starts.", nameof(endsAt));
        }

        DelegatorId = delegatorId;
        DelegateId = delegateId;
        Duties = [.. duties.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        StartsAt = startsAt;
        EndsAt = endsAt;
        Reason = reason.Trim()[..Math.Min(reason.Trim().Length, ReasonMaxLength)];
    }

    public Guid DelegatorId { get; private set; }

    public Guid DelegateId { get; private set; }

    /// <summary>The duty codes, sorted; <c>all</c> alone when it is all.</summary>
    public List<string> Duties { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    public string Reason { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public Guid? EndedBy { get; private set; }

    public string? EndReason { get; private set; }

    /// <summary>When the delegation stops counting: its end, or the moment someone ended it early.</summary>
    public DateTimeOffset Until => EndedAt is { } ended && ended < EndsAt ? ended : EndsAt;

    public bool CoversAll => Duties.Contains(KernelDuties.AllCode, StringComparer.Ordinal);

    public DelegationStatus StatusAt(DateTimeOffset now)
    {
        if (EndedAt is not null && EndedAt < EndsAt)
        {
            return DelegationStatus.Ended;
        }

        if (now >= EndsAt)
        {
            return DelegationStatus.Expired;
        }

        return now < StartsAt ? DelegationStatus.Scheduled : DelegationStatus.Active;
    }

    /// <summary>Whether it counts at some moment of the period [from, to).</summary>
    public bool Overlaps(DateTimeOffset from, DateTimeOffset to) => StartsAt < to && from < Until;

    /// <summary>Whether it hands at least one of these duties: <c>all</c> shares every duty with everything.</summary>
    public bool SharesDutyWith(IReadOnlyCollection<string> duties)
    {
        ArgumentNullException.ThrowIfNull(duties);
        return CoversAll || duties.Contains(KernelDuties.AllCode, StringComparer.Ordinal) || Duties.Intersect(duties, StringComparer.Ordinal).Any();
    }

    public void End(DateTimeOffset now, Guid by, string? reason)
    {
        if (EndedAt is not null || now >= EndsAt)
        {
            return;
        }

        EndedAt = now < StartsAt ? StartsAt : now;
        EndedBy = by;
        EndReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, ReasonMaxLength)];
    }
}
