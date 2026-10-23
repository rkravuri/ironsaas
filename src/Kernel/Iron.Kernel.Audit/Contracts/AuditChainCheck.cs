using System.Globalization;

namespace Iron.Kernel.Audit.Contracts;

/// <summary>A sealed event as the verifier reads it: its content, and the chain columns the sealer set.</summary>
public sealed record SealedEvent(ChainedEvent Content, string PreviousHash, string Hash, short HashVersion, DateTimeOffset SealedAt)
{
    public long Position => Content.Position;
}

/// <summary>Something wrong at a position: a missing event, a changed one, a broken link, or a disagreement with an anchor.</summary>
public sealed record ChainProblem(string Kind, long Position, Guid? EventId, string Message);

public static class ChainProblemKinds
{
    /// <summary>A position with no event: the event there was deleted.</summary>
    public const string Missing = "missing";

    /// <summary>An event whose content no longer gives its hash: it was changed.</summary>
    public const string Modified = "modified";

    /// <summary>An event whose previous hash is not the hash before it: the chain was relinked.</summary>
    public const string Relinked = "relinked";

    /// <summary>An event whose hash differs from the anchor the control plane keeps for its position.</summary>
    public const string AnchorMismatch = "anchor_mismatch";

    /// <summary>A hash made with a canonical form this verifier does not know.</summary>
    public const string UnknownVersion = "unknown_version";
}

/// <summary>What a check found: the range, the hash it ended on, its problems, and a restore point when the chain ends before its anchors.</summary>
public sealed record ChainCheckResult(
    long FromPosition,
    long ToPosition,
    string ToHash,
    int EventsChecked,
    IReadOnlyList<ChainProblem> Problems,
    long? RestorePosition,
    DateTimeOffset? RestoreAt)
{
    public bool Passed => Problems.Count == 0 && RestorePosition is null;

    public bool Restored => RestorePosition is not null;
}

/// <summary>
/// The verification of a tenant's chain (TF-061, ADR-0050), as the fleet runs it with its own read-only role. From a
/// starting position and the hash before it, every event must follow at the next position, link to the hash before
/// it, and recompute to its own hash from its content; every anchor in the range must match. A missing position is a
/// deleted event; a mismatch names the event. When the chain ends before the latest anchor, the database was restored:
/// the restore point is the last position whose hash matches an anchor, with that anchor's time (AC3). After a restore an
/// operator accepted, the chain continues as a new segment: its first event must link to the restore point's hash, and
/// each anchor counts only in the segment that covers its position, so the range the restore lost is no disagreement.
/// </summary>
public static class AuditChainCheck
{
    public static ChainCheckResult Check(long startPosition, string startPreviousHash, IEnumerable<SealedEvent> events, IReadOnlyList<AuditAnchor> anchors, IReadOnlyList<ChainSegment>? segments = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentException.ThrowIfNullOrWhiteSpace(startPreviousHash);
        segments ??= [];
        // Two restores to the same point make two segments from one position: the later one stands.
        var starts = segments.GroupBy(s => s.StartPosition).ToDictionary(g => g.Key, g => g.MaxBy(s => s.Number)!);
        var problems = new List<ChainProblem>();
        var hashes = new Dictionary<long, (string Hash, Guid Id)>();
        var expected = startPosition;
        var previous = startPreviousHash;
        Guid? previousId = null;
        var checkedCount = 0;

        foreach (var sealedEvent in events.OrderBy(e => e.Position))
        {
            if (sealedEvent.Position < expected)
            {
                continue;
            }

            var afterGap = false;
            if (sealedEvent.Position > expected)
            {
                var missing = sealedEvent.Position - expected;
                problems.Add(new ChainProblem(
                    ChainProblemKinds.Missing,
                    expected,
                    null,
                    missing == 1
                        ? $"Position {expected} is missing: the event between {Describe(previousId)} and {sealedEvent.Content.Id:D} was deleted."
                        : $"Positions {expected} to {sealedEvent.Position - 1} are missing: {missing.ToString(CultureInfo.InvariantCulture)} events between {Describe(previousId)} and {sealedEvent.Content.Id:D} were deleted."));
                afterGap = true;
            }

            checkedCount++;
            if (!AuditCanonicalForm.Knows(sealedEvent.HashVersion))
            {
                problems.Add(new ChainProblem(ChainProblemKinds.UnknownVersion, sealedEvent.Position, sealedEvent.Content.Id, $"Event {sealedEvent.Content.Id:D} was hashed with canonical form {sealedEvent.HashVersion}, which this verifier does not know."));
            }
            else if (!afterGap && !string.Equals(sealedEvent.PreviousHash, previous, StringComparison.Ordinal))
            {
                problems.Add(new ChainProblem(ChainProblemKinds.Relinked, sealedEvent.Position, sealedEvent.Content.Id, $"Event {sealedEvent.Content.Id:D} at position {sealedEvent.Position} does not link to the hash of the event before it."));
            }
            else if (!string.Equals(AuditCanonicalForm.Hash(sealedEvent.Content, sealedEvent.PreviousHash, sealedEvent.HashVersion), sealedEvent.Hash, StringComparison.Ordinal))
            {
                problems.Add(new ChainProblem(ChainProblemKinds.Modified, sealedEvent.Position, sealedEvent.Content.Id, $"Event {sealedEvent.Content.Id:D} at position {sealedEvent.Position} was changed after it was sealed: its content no longer gives its hash."));
            }
            else if (starts.TryGetValue(sealedEvent.Position, out var segment) && !string.Equals(sealedEvent.PreviousHash, segment.PreviousHash, StringComparison.Ordinal))
            {
                problems.Add(new ChainProblem(ChainProblemKinds.Relinked, sealedEvent.Position, sealedEvent.Content.Id, $"Event {sealedEvent.Content.Id:D} starts segment {segment.Number} but does not link to the restore point the operator accepted."));
            }

            hashes[sealedEvent.Position] = (sealedEvent.Hash, sealedEvent.Content.Id);
            previous = sealedEvent.Hash;
            previousId = sealedEvent.Content.Id;
            expected = sealedEvent.Position + 1;
        }

        var end = expected - 1;
        // An anchor in a segment that a later one covers marks a position a restore lost: a record, not a comparison.
        var seals = anchors.Where(a => a.Kind == AuditAnchorKinds.Seal && AuditChainSegments.Counts(a, segments)).OrderBy(a => a.Position).ToList();
        foreach (var anchor in seals.Where(a => a.Position >= startPosition && a.Position <= end))
        {
            if (hashes.TryGetValue(anchor.Position, out var found) && !string.Equals(found.Hash, anchor.Hash, StringComparison.Ordinal))
            {
                problems.Add(new ChainProblem(ChainProblemKinds.AnchorMismatch, anchor.Position, found.Id, $"Event {found.Id:D} at position {anchor.Position} differs from the anchor the control plane took at {AuditCanonicalForm.Time(anchor.At)}."));
            }
        }

        long? restorePosition = null;
        DateTimeOffset? restoreAt = null;
        var latest = seals.LastOrDefault();
        if (latest is not null && (latest.Position > end || problems.Any(p => p.Kind == ChainProblemKinds.AnchorMismatch)))
        {
            // The chain ends before its latest anchor, or was resealed after one: the last anchor it still matches is
            // where a restored backup ends. Before the range, the starting hash stands for the chain verified earlier.
            var matching = seals.Where(a => a.Position <= end && Matches(a)).LastOrDefault();
            if (matching is not null)
            {
                restorePosition = matching.Position;
                restoreAt = matching.At;
            }
            else
            {
                restorePosition = startPosition - 1;
                restoreAt = seals.Where(a => a.Position <= startPosition - 1).Select(a => (DateTimeOffset?)a.At).LastOrDefault();
            }
        }

        return new ChainCheckResult(startPosition, end, previous, checkedCount, problems, restorePosition, restoreAt);

        bool Matches(AuditAnchor anchor) =>
            anchor.Position < startPosition || (hashes.TryGetValue(anchor.Position, out var found) && string.Equals(found.Hash, anchor.Hash, StringComparison.Ordinal));
    }

    private static string Describe(Guid? id) => id is { } value ? value.ToString("D") : "the start of the range";
}
