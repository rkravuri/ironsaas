using Iron.Kernel.History;

namespace Iron.Kernel.Monitoring.Contracts;

/// <summary>The Monitoring module's permissions (ADR-0056).</summary>
public static class MonitoringPermissions
{
    public static readonly Permission AlertsRead = new("monitoring.alerts.read", "See the security alerts and the alert rules, with their thresholds and whether they run.", Risk.Medium);

    public static readonly Permission AlertsManage = new("monitoring.alerts.manage", "Acknowledge and resolve security alerts.", Risk.High);

    /// <summary>A tenant's values may only make a rule stricter (ADR-0056); loosening one is the operator's.</summary>
    public static readonly Permission RulesManage = new("monitoring.rules.manage", "Make the alert rules stricter than the platform operator's values, or return them to those values.", Risk.High);

    public static IReadOnlyList<Permission> All => [AlertsRead, AlertsManage, RulesManage];
}

/// <summary>The Monitoring module's audit events (ADR-0056).</summary>
public static class MonitoringAuditEvents
{
    public const string AlertRaised = "monitoring.alert_raised";
    public const string AlertAcknowledged = "monitoring.alert_acknowledged";
    public const string AlertResolved = "monitoring.alert_resolved";

    /// <summary>A tenant changed a rule's values, or returned them to the operator's.</summary>
    public const string RuleChanged = "monitoring.rule_changed";

    /// <summary>A rule could not run: its definition is invalid, its evaluation failed, or it missed its minutes (TF-079 AC4).</summary>
    public const string RuleFailing = "monitoring.rule_failing";

    public const string RuleRecovered = "monitoring.rule_recovered";

    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        new(AlertRaised, "Security alert raised"),
        new AuditEventType(AlertAcknowledged, "Security alert acknowledged").AsAdministrative(),
        new AuditEventType(AlertResolved, "Security alert resolved").AsAdministrative(),
        new AuditEventType(RuleChanged, "Alert rule changed").AsAdministrative(),
        new(RuleFailing, "Alert rule failing"),
        new(RuleRecovered, "Alert rule running again"),
    ];
}

public static class MonitoringErrors
{
    public const string AlertNotFound = "monitoring.alert.not_found";

    /// <summary>An alert copied from a unit is acknowledged and resolved in the unit, where its events are.</summary>
    public const string AlertIsACopy = "monitoring.alert.copy";

    public const string AlertResolved = "monitoring.alert.resolved";
    public const string NoteRequired = "monitoring.alert.note_required";
    public const string RuleNotFound = "monitoring.rule.not_found";

    /// <summary>A tenant's value would make the rule looser than the operator's, or leaves the rule's bounds (ADR-0056).</summary>
    public const string RuleValueLooser = "monitoring.rule.looser";

    public const string RuleValueInvalid = "monitoring.rule.invalid";
}

/// <summary>Where the module runs (ADR-0056): in a tenant's unit, over its trail, or in the control plane, over the platform's.</summary>
public enum MonitoringScope
{
    Unit,
    ControlPlane,
}

/// <summary>An alert's severity, which the operator's alerting routes by.</summary>
public static class AlertSeverity
{
    public const string Critical = "critical";
    public const string High = "high";
    public const string Normal = "normal";

    public static IReadOnlyList<string> All => [Critical, High, Normal];
}

/// <summary>An alert's state: open, acknowledged by someone who is on it, or resolved with a note.</summary>
public static class AlertStates
{
    public const string Open = "open";
    public const string Acknowledged = "acknowledged";
    public const string Resolved = "resolved";
}

/// <summary>What raised an alert: a rule's threshold reached, or the rule itself failing (TF-079 AC4).</summary>
public static class AlertKinds
{
    public const string Threshold = "threshold";
    public const string Failing = "failing";
}

/// <summary>
/// A unit's alert as the control plane keeps it for the operator (ADR-0056): what the unit sends after every change,
/// with its revision, so an older copy never overwrites a newer one. A person is named by their id, a login identifier
/// is masked: the person's details stay in the tenant.
/// </summary>
public sealed record AlertCopy(
    Guid Id,
    int Revision,
    string Rule,
    string Title,
    string Kind,
    string Severity,
    string State,
    string GroupKind,
    string GroupValue,
    long Count,
    int Threshold,
    int WindowMinutes,
    DateTimeOffset? FirstEventAt,
    DateTimeOffset? LastEventAt,
    DateTimeOffset RaisedAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ResolvedAt,
    string? Reason,
    string? Link,
    string? Runbook);

/// <summary>The control plane's store of the units' alerts, which its alerting forwards (ADR-0056); Tenancy hands them in.</summary>
public interface IAlertCopies
{
    /// <summary>Keeps the copy for the tenant: new, or newer than the one kept; an older revision changes nothing.</summary>
    Task KeepAsync(string tenant, AlertCopy copy, CancellationToken cancellationToken);
}
