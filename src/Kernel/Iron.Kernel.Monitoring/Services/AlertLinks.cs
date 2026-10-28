using System.Globalization;
using Iron.Kernel.Audit.Contracts;

namespace Iron.Kernel.Monitoring.Services;

/// <summary>
/// Where an alert leads (ADR-0056): the shell's audit search, and the same search on the API, over the rule's event types,
/// the alert's group and the time from its first event to its last, both included. A rule whose events must carry a
/// detail or a target the search cannot filter by (a critical role change, the MFA policy's settings) shows a few more
/// of the same type and person around them; every one it counted is there.
/// </summary>
public static class AlertLinks
{
    /// <summary>The record type of an alert's own audit events.</summary>
    public const string RecordType = "security_alert";

    /// <summary>The group of an alert a failing rule raised: the rule.</summary>
    public const string RuleGroup = "rule";

    public static string GroupKind(AuditGrouping grouping) => grouping switch
    {
        AuditGrouping.Principal => "principal",
        AuditGrouping.Address => "address",
        AuditGrouping.Target => "target",
        AuditGrouping.Record => "record",
        _ => "all",
    };

    /// <summary>The shell's audit search: <c>/audit?event_type=a,b&amp;actor_id=…&amp;since=…&amp;until=…</c>.</summary>
    public static string Shell(AlertRule rule, string group, DateTimeOffset first, DateTimeOffset last)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var parameters = new List<string> { $"event_type={Escape(Types(rule))}" };
        if (GroupParameter(rule.GroupBy) is { } name)
        {
            parameters.Add($"{name}={Escape(group)}");
        }

        parameters.Add($"since={Escape(Instant(first))}");
        parameters.Add($"until={Escape(Instant(last))}");
        return "/audit?" + string.Join('&', parameters);
    }

    /// <summary>The same search on the API, newest first.</summary>
    public static string Api(AlertRule rule, string group, DateTimeOffset first, DateTimeOffset last)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var types = rule.Matches.Select(m => m.EventType).Distinct(StringComparer.Ordinal).ToList();
        var parameters = new List<string> { types.Count == 1 ? $"filter=event_type:eq:{Escape(types[0])}" : $"filter=event_type:in:{Escape(string.Join(',', types))}" };
        switch (rule.GroupBy)
        {
            case AuditGrouping.Principal:
                parameters.Add($"filter=actor_id:eq:{Escape(group)}");
                break;
            case AuditGrouping.Target:
                parameters.Add($"filter=target:eq:{Escape(group)}");
                break;
            case AuditGrouping.Record:
                parameters.Add($"filter=record_id:eq:{Escape(group)}");
                break;
            case AuditGrouping.Address:
                parameters.Add($"address={Escape(group)}");
                break;
            case AuditGrouping.Everything:
            default:
                break;
        }

        parameters.Add($"filter=created_at:gte:{Escape(Instant(first))}");
        parameters.Add($"filter=created_at:lte:{Escape(Instant(last))}");
        parameters.Add("sort=-created_at");
        parameters.Add("page_size=100");
        return "/api/v1/audit-events?" + string.Join('&', parameters);
    }

    /// <summary>A failing rule's alert leads to the rules page, where the rule shows why.</summary>
    public static string RulePage(string rule) => $"/console/alert-rules?rule={Escape(rule)}";

    /// <summary>The name of the shell's parameter for a group, none for the whole trail.</summary>
    public static string? GroupParameter(AuditGrouping grouping) => grouping switch
    {
        AuditGrouping.Principal => "actor_id",
        AuditGrouping.Address => "address",
        AuditGrouping.Target => "target",
        AuditGrouping.Record => "record_id",
        _ => null,
    };

    private static string Types(AlertRule rule) => string.Join(',', rule.Matches.Select(m => m.EventType).Distinct(StringComparer.Ordinal));

    private static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
