namespace Iron.Kernel.Audit.Contracts;

/// <summary>What a count of audit events is grouped by (ADR-0056): the actor, the client's address, the target, the record, or none.</summary>
public enum AuditGrouping
{
    /// <summary>The actor, never the anonymous one: a denial or an export by one principal.</summary>
    Principal,

    /// <summary>The client's address the event recorded in its details (<see cref="AuditDetails.Address"/>).</summary>
    Address,

    /// <summary>The target: the login identifier of a sign-in, the tenant of an operator's action.</summary>
    Target,

    /// <summary>The record the event is about, such as an incident.</summary>
    Record,

    /// <summary>One group for the whole trail: the tenant, or the control plane.</summary>
    Everything,
}

/// <summary>
/// One kind of event a count takes (ADR-0056): its type, and, when given, the outcome it had, a detail it carries with a
/// value, or a target it starts with, such as the settings of the MFA policy (<c>identity.mfa.</c>).
/// </summary>
public sealed record AuditEventMatch(string EventType, string? Outcome = null, string? DetailKey = null, string? DetailValue = null, string? TargetPrefix = null);

/// <summary>
/// A count of audit events over a window (ADR-0056): those that match any of <paramref name="Matches"/>, written after
/// <paramref name="After"/> and up to <paramref name="Until"/>, by group. With <paramref name="QuantityKey"/> only the
/// events whose detail holds a whole number above <paramref name="Above"/> count, such as an export of more than 10,000
/// rows; with <paramref name="SumKey"/> the detail's numbers are added up rather than the events counted, such as the
/// reports a summary event stands for. With <paramref name="Group"/> only that group is counted.
/// </summary>
public sealed record AuditCountQuery(
    IReadOnlyList<AuditEventMatch> Matches,
    AuditGrouping GroupBy,
    DateTimeOffset After,
    DateTimeOffset Until,
    string? QuantityKey = null,
    long? Above = null,
    string? SumKey = null,
    string? Group = null);

/// <summary>
/// One group of a count: its value (the actor's id, the address, the target, the record's id, or <c>all</c>), the
/// events it holds, what they add up to (the events again, or the sum of <see cref="AuditCountQuery.SumKey"/>), and the
/// first and last of them.
/// </summary>
public sealed record AuditGroupCount(string Group, int Events, long Measure, DateTimeOffset First, DateTimeOffset Last);

/// <summary>
/// Counts the audit trail for the security alert rules (TF-079, ADR-0056): every authentication failure, denial,
/// impersonation, break-glass use, export and configuration change is an event with its actor, so a rule counts events
/// rather than metrics, which would need a label for every person. Reads the online part of the trail only, which holds
/// months; a rule's window is minutes or hours.
/// </summary>
public interface IAuditCounts
{
    Task<IReadOnlyList<AuditGroupCount>> CountAsync(AuditCountQuery query, CancellationToken cancellationToken);
}

/// <summary>The details keys the alert rules read (ADR-0056); a module that writes one uses these names.</summary>
public static class AuditDetails
{
    /// <summary>The client's address, as <c>ClientAddress</c> resolves it behind the ingress.</summary>
    public const string Address = "address";

    /// <summary>How many records an export wrote (TF-079 AC3).</summary>
    public const string Rows = "rows";

    /// <summary>"true" on a change of a role that holds or held a critical permission, or an assignment of one.</summary>
    public const string Critical = "critical";

    /// <summary>How many reports a summary event stands for, such as the content security policy's.</summary>
    public const string Reports = "reports";

    /// <summary>The value <see cref="Critical"/> holds when it applies.</summary>
    public const string True = "true";
}
