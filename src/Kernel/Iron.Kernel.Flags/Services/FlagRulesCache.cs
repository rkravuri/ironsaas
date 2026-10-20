namespace Iron.Kernel.Flags.Services;

/// <summary>The rules a unit holds, and when the store last confirmed them.</summary>
public sealed record FlagSnapshot(FlagRuleSet Rules, DateTimeOffset ConfirmedAt);

/// <summary>
/// The unit's current rules, shared by every evaluation (ADR-0035). Unlike entitlements they never expire: last known
/// rules stand for as long as the store does not answer, so a kill switch cannot switch itself off in an outage. Only
/// when nothing was ever known do the code defaults apply, and that is the alert.
/// </summary>
public sealed class FlagRulesCache
{
    private readonly Lock _gate = new();
    private FlagSnapshot? _current;
    private DateTimeOffset? _lastFailureAt;
    private string? _lastFailure;

    /// <summary>The rules in force, or null when nothing is known and the code defaults apply.</summary>
    public FlagSnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

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

    public void Confirm(FlagRuleSet rules, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(rules);
        lock (_gate)
        {
            _current = new FlagSnapshot(rules, at);
            _lastFailureAt = null;
            _lastFailure = null;
        }
    }

    /// <summary>The last known rules from the database, after a restart; a fresher answer already held wins.</summary>
    public bool Restore(FlagRuleSet rules, DateTimeOffset confirmedAt)
    {
        ArgumentNullException.ThrowIfNull(rules);
        lock (_gate)
        {
            if (_current is not null && _current.ConfirmedAt >= confirmedAt)
            {
                return false;
            }

            _current = new FlagSnapshot(rules, confirmedAt);
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
}
