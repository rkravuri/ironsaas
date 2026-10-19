using Iron.Kernel.Entitlements.Contracts;

namespace Iron.Kernel.Entitlements.Services;

/// <summary>An answer of the store and when the store last confirmed it.</summary>
public sealed record EntitlementSnapshot(EntitlementsDocument Document, DateTimeOffset ConfirmedAt);

/// <summary>
/// The unit's current answer, shared by every request (TF-043): the refresh replaces it, the evaluator reads it. Up to
/// <see cref="EntitlementsOptions.StaleAfter"/> old it stands even when the store does not answer; older, or never
/// confirmed, everything licensed answers Disabled with the reason <c>unknown</c> and the alert is raised.
/// </summary>
public sealed class EntitlementCache
{
    private readonly Lock _gate = new();
    private EntitlementSnapshot? _current;
    private DateTimeOffset? _lastFailureAt;
    private string? _lastFailure;

    public EntitlementSnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>When the last read failed and why, or null after a read that worked.</summary>
    public (DateTimeOffset At, string Message)? LastFailure
    {
        get
        {
            lock (_gate)
            {
                return _lastFailureAt is { } at ? (at, _lastFailure ?? string.Empty) : null;
            }
        }
    }

    /// <summary>The store answered.</summary>
    public void Confirm(EntitlementsDocument document, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            _current = new EntitlementSnapshot(document, at);
            _lastFailureAt = null;
            _lastFailure = null;
        }
    }

    /// <summary>The last known answer from the database, after a restart; a fresher answer already held wins.</summary>
    public bool Restore(EntitlementsDocument document, DateTimeOffset confirmedAt)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            if (_current is not null && _current.ConfirmedAt >= confirmedAt)
            {
                return false;
            }

            _current = new EntitlementSnapshot(document, confirmedAt);
            return true;
        }
    }

    public void Fail(string message, DateTimeOffset at)
    {
        lock (_gate)
        {
            _lastFailureAt ??= at;
            _lastFailure = message;
        }
    }

    /// <summary>The answer that applies at this moment: null when there is none or it is older than the limit.</summary>
    public EntitlementSnapshot? Usable(DateTimeOffset now, TimeSpan staleAfter)
    {
        var current = Current;
        return current is not null && now - current.ConfirmedAt <= staleAfter ? current : null;
    }
}
