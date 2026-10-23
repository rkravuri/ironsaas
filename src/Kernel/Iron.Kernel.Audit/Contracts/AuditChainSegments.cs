namespace Iron.Kernel.Audit.Contracts;

/// <summary>
/// A segment of a tenant's chain (ADR-0050). The first starts at position 1 from the genesis hash. After a restore an
/// operator accepted, the chain continues as a new segment from the position after the restore point, its first event
/// linking to the restore point's hash; the positions the restore lost stay anchored in the segment before, as a record.
/// </summary>
public sealed record ChainSegment(int Number, long StartPosition, string PreviousHash);

/// <summary>What accepting a restore makes: the new segment, the restore point it continues from, and the range it lost.</summary>
public sealed record AcceptedRestore(ChainSegment Segment, long RestorePosition, DateTimeOffset RestoreAt, long LostFrom, long LostTo);

/// <summary>
/// The rules of segments, the same for the control plane that keys anchors by them and the fleet that verifies by them
/// (ADR-0050). A position belongs to the latest segment that starts at or before it; an anchor counts where its own
/// segment covers its position, so a lost position's old anchor stays a record and is never compared again.
/// </summary>
public static class AuditChainSegments
{
    /// <summary>The segment every chain begins with, from position 1.</summary>
    public const int First = 1;

    /// <summary>The segment that covers a position.</summary>
    public static int Covering(long position, IReadOnlyList<ChainSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return segments.Where(s => s.StartPosition <= position).Select(s => s.Number).DefaultIfEmpty(First).Max();
    }

    /// <summary>The segment new positions go to: the latest.</summary>
    public static int Current(IReadOnlyList<ChainSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return segments.Select(s => s.Number).DefaultIfEmpty(First).Max();
    }

    /// <summary>Whether an anchor counts: its segment is the one that covers its position now.</summary>
    public static bool Counts(AuditAnchor anchor, IReadOnlyList<ChainSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return anchor.Segment == Covering(anchor.Position, segments);
    }

    /// <summary>
    /// The segment accepting a restore makes: it starts after the restore point and links to the anchor there; the range
    /// lost runs from the position after the point to the latest anchor that counted. Null when no anchor counts at the
    /// restore point, which a full verification finds.
    /// </summary>
    public static AcceptedRestore? Accept(long restorePosition, IReadOnlyList<AuditAnchor> anchors, IReadOnlyList<ChainSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(segments);
        var counting = anchors.Where(a => Counts(a, segments)).ToList();
        var point = counting.Where(a => a.Position == restorePosition).OrderBy(a => a.Kind == AuditAnchorKinds.Seal ? 0 : 1).FirstOrDefault();
        if (point is null)
        {
            return null;
        }

        var lostTo = counting.Where(a => a.Kind == AuditAnchorKinds.Seal && a.Position > restorePosition).Select(a => a.Position).DefaultIfEmpty(restorePosition).Max();
        var segment = new ChainSegment(Current(segments) + 1, restorePosition + 1, point.Hash);
        return new AcceptedRestore(segment, restorePosition, point.At, restorePosition + 1, lostTo);
    }
}
