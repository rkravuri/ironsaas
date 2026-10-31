using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Monitoring.Contracts;
using Microsoft.Extensions.Configuration;

namespace Iron.Kernel.Monitoring.Services;

/// <summary>Where a rule runs: in each tenant's unit, in the control plane, or both.</summary>
[Flags]
public enum RuleScopes
{
    Unit = 1,
    ControlPlane = 2,
    Both = Unit | ControlPlane,
}

/// <summary>
/// A security alert rule as the kernel ships it (TF-079, ADR-0056): the audit events it counts, how it groups them, the
/// threshold within the window, the severity, and the cool-down within which more of the same burst joins the alert it
/// raised rather than raising another. A rule with a quantity counts only events whose detail is above it (an export of
/// more than 10,000 rows); a rule with a sum adds up the detail (the reports a summary stands for). A threshold of one
/// raises an alert for each new event at once, and such a rule catches up after a gap rather than missing what happened.
/// </summary>
public sealed record AlertRule(
    string Name,
    string Title,
    IReadOnlyList<AuditEventMatch> Matches,
    AuditGrouping GroupBy,
    int Threshold,
    int WindowMinutes,
    int CooldownMinutes,
    string Severity,
    RuleScopes Scopes,
    string Runbook,
    string? QuantityKey = null,
    long? Above = null,
    string? SumKey = null)
{
    /// <summary>Each event matters: a rule with a threshold of one that weighs no detail.</summary>
    public bool AtOnce => Threshold == 1 && QuantityKey is null && SumKey is null;

    /// <summary>What a tenant may make stricter: the threshold and the window of a counting rule, the quantity of a weighing one.</summary>
    public bool ThresholdEditable => !AtOnce && QuantityKey is null;

    public bool QuantityEditable => QuantityKey is not null;
}

/// <summary>The rules the kernel ships, with their defaults and the runbook's section for each (ADR-0056).</summary>
public static class AlertRules
{
    public const string RunbookPath = "docs/security/incident-response.md";

    /// <summary>The bounds every value keeps, whoever sets it.</summary>
    public const int MaximumThreshold = 100_000;

    public const int MaximumWindowMinutes = 1440;

    public const int MaximumCooldownMinutes = 10_080;

    public const long MaximumAbove = 1_000_000_000_000;

    private static readonly string[] RoleEvents = ["authz.role_created", "authz.role_changed", "authz.role_deleted", "authz.role_assigned", "authz.role_unassigned"];

    public static IReadOnlyList<AlertRule> All { get; } =
    [
        new("permission_denials", "Many permission denials for one principal",
            [new("authz.permission_denied")], AuditGrouping.Principal, 100, 10, 60, AlertSeverity.High, RuleScopes.Both, Runbook("permission-denials")),
        new("sign_in_failures_by_address", "Many failed sign-ins from one address",
            [new("identity.login_failed")], AuditGrouping.Address, 30, 10, 60, AlertSeverity.High, RuleScopes.Unit, Runbook("sign-in-failures-and-lockouts")),
        new("sign_in_failures_by_login", "Many failed sign-ins for one login identifier",
            [new("identity.login_failed")], AuditGrouping.Target, 10, 10, 60, AlertSeverity.Normal, RuleScopes.Unit, Runbook("sign-in-failures-and-lockouts")),
        new("lockouts_by_address", "Several login identifiers locked from one address",
            [new("identity.login_locked")], AuditGrouping.Address, 3, 60, 60, AlertSeverity.High, RuleScopes.Unit, Runbook("sign-in-failures-and-lockouts")),
        new("lockouts_by_login", "A login identifier locked",
            [new("identity.login_locked")], AuditGrouping.Target, 1, 15, 60, AlertSeverity.Normal, RuleScopes.Unit, Runbook("sign-in-failures-and-lockouts")),
        new("impersonations", "Someone signed in as another person",
            [new("identity.impersonation_started")], AuditGrouping.Principal, 1, 10, 0, AlertSeverity.High, RuleScopes.Unit, Runbook("impersonation-break-glass-and-emergency-accounts")),
        new("break_glass", "A break-glass account opened or used",
            [new("identity.break_glass_opened"), new("identity.login_succeeded", DetailKey: "method", DetailValue: "break_glass")], AuditGrouping.Everything, 1, 10, 0, AlertSeverity.Critical, RuleScopes.Unit, Runbook("impersonation-break-glass-and-emergency-accounts")),
        new("emergency_sign_ins", "An emergency account signed in",
            [new("identity.emergency_sign_in")], AuditGrouping.Principal, 1, 10, 0, AlertSeverity.High, RuleScopes.Unit, Runbook("impersonation-break-glass-and-emergency-accounts")),
        new("mass_exports", "An export of more than 10,000 records",
            [new("audit.events_exported"), new("authz.roles_exported"), new("identity.acceptances_exported")], AuditGrouping.Principal, 1, 10, 0, AlertSeverity.High, RuleScopes.Both, Runbook("mass-exports"),
            QuantityKey: AuditDetails.Rows, Above: 10_000),
        new("critical_configuration_changes", "A change in a critical area of configuration",
            [
                .. RoleEvents.Select(type => new AuditEventMatch(type, AuditOutcome.Success, AuditDetails.Critical, AuditDetails.True)),
                new("identity.sso_provider_created", AuditOutcome.Success),
                new("identity.sso_provider_changed", AuditOutcome.Success),
                new("identity.sso_provider_deleted", AuditOutcome.Success),
                new("keys.key_rotated"),
                new("keys.key_retired"),
                new("audit.retention_changed"),
                new("audit.legal_hold_placed"),
                new("audit.legal_hold_released"),
                new("settings.value_changed", TargetPrefix: "identity.mfa."),
                new("settings.value_reset", TargetPrefix: "identity.mfa."),
                new(MonitoringAuditEvents.RuleChanged),
            ],
            AuditGrouping.Principal, 1, 10, 0, AlertSeverity.High, RuleScopes.Unit, Runbook("critical-configuration-changes")),
        new("rate_limit_refusals", "Requests from one address refused by the rate limits again and again",
            [new("protection.rate_limited")], AuditGrouping.Address, 10, 60, 60, AlertSeverity.Normal, RuleScopes.Unit, Runbook("rate-limits-and-policy-reports")),
        new("csp_reports", "Many reports of the content security policy",
            [new("protection.csp_reports_received")], AuditGrouping.Everything, 100, 10, 60, AlertSeverity.Normal, RuleScopes.Unit, Runbook("rate-limits-and-policy-reports"),
            SumKey: AuditDetails.Reports),

        // A subscriber that failed an event as many times as it is retried (TF-085 AC2, ADR-0060): one alert per subscriber's burst.
        new("event_dead_letters", "A domain event parked as a dead letter",
            [new("events.delivery_dead_lettered")], AuditGrouping.Target, 1, 10, 60, AlertSeverity.High, RuleScopes.Unit, Runbook("dead-lettered-events")),

        // The control plane's own trail: what operators do, and what the fleet finds.
        new("operator_access_changes", "A change of the operators' critical access",
            [.. RoleEvents.Select(type => new AuditEventMatch(type, AuditOutcome.Success, AuditDetails.Critical, AuditDetails.True)), new("tenancy.key_rotation_requested"), new("tenancy.audit_restore_accepted")],
            AuditGrouping.Principal, 1, 10, 0, AlertSeverity.High, RuleScopes.ControlPlane, Runbook("critical-configuration-changes")),
        new("break_glass_requests", "An operator asked for break-glass access to a tenant",
            [new("tenancy.break_glass_requested")], AuditGrouping.Record, 1, 10, 0, AlertSeverity.Critical, RuleScopes.ControlPlane, Runbook("impersonation-break-glass-and-emergency-accounts")),
        new("operator_impersonations", "An operator asked to sign in as a tenant's person",
            [new("tenancy.impersonation_requested")], AuditGrouping.Target, 1, 10, 0, AlertSeverity.High, RuleScopes.ControlPlane, Runbook("impersonation-break-glass-and-emergency-accounts")),
        new("audit_chain_breaks", "A tenant's audit chain broken or disputed",
            [new("tenancy.audit_chain_broken"), new("tenancy.audit_anchor_conflict")], AuditGrouping.Target, 1, 10, 60, AlertSeverity.Critical, RuleScopes.ControlPlane, Runbook("audit-chain-and-keys")),
        new("key_rotation_failures", "A key rotation failed",
            [new("tenancy.key_rotation_failed")], AuditGrouping.Target, 1, 10, 0, AlertSeverity.High, RuleScopes.ControlPlane, Runbook("audit-chain-and-keys")),
        new("incident_reviews_overdue", "A post-incident review not recorded within 10 days",
            [new("tenancy.incident_review_overdue")], AuditGrouping.Record, 1, 60, 0, AlertSeverity.High, RuleScopes.ControlPlane, Runbook("the-post-incident-review")),
        new("incident_notices_overdue", "A personal data breach not notified within 72 hours",
            [new("tenancy.incident_notice_overdue")], AuditGrouping.Record, 1, 60, 0, AlertSeverity.Critical, RuleScopes.ControlPlane, Runbook("the-72-hour-notice")),
    ];

    /// <summary>The runbook's section that a failing rule's alert points to.</summary>
    public static string FailingRunbook { get; } = Runbook("a-failing-rule");

    public static IReadOnlyList<AlertRule> For(MonitoringScope scope) =>
        [.. All.Where(rule => rule.Scopes.HasFlag(scope == MonitoringScope.Unit ? RuleScopes.Unit : RuleScopes.ControlPlane))];

    public static AlertRule? Find(MonitoringScope scope, string name) => For(scope).FirstOrDefault(rule => rule.Name == name);

    private static string Runbook(string section) => $"{RunbookPath}#{section}";
}

/// <summary>The values a tenant chose for a rule; each null keeps the operator's.</summary>
public sealed record TenantRuleValues(int? Threshold, int? WindowMinutes, long? Above);

/// <summary>
/// A rule as it runs: the kernel's defaults with the operator's values (<c>Monitoring:Rules:&lt;rule&gt;:*</c>) and the
/// tenant's stricter ones applied. <see cref="Operator"/> is the operator's view, which bounds the tenant's.
/// </summary>
public sealed record ResolvedRule(AlertRule Rule, AlertRule Operator, int Threshold, int WindowMinutes, long? Above)
{
    public AlertRule Effective => Rule with { Threshold = Threshold, WindowMinutes = WindowMinutes, Above = Above };
}

/// <summary>
/// Works out what a rule runs with, or why it cannot run (TF-079 AC4). The operator's values come from configuration as
/// text: a value that is no whole number, or outside the rule's bounds, an unknown severity, or an event type no module of
/// the host declares, makes the rule fail with that reason rather than run with a guess. A tenant's value is applied only
/// where it is stricter, so an operator who tightens a default later tightens every tenant with it.
/// </summary>
public static class AlertRuleResolver
{
    public const string Section = "Monitoring:Rules";

    private static readonly string[] Keys = ["Threshold", "WindowMinutes", "CooldownMinutes", "Above", "Severity"];

    public static Result<ResolvedRule> Resolve(AlertRule rule, IConfiguration configuration, TenantRuleValues? tenant, IReadOnlySet<string> declaredTypes)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(declaredTypes);
        var section = configuration.GetSection($"{Section}:{rule.Name}");
        var problems = new List<string>();
        var threshold = Number(section, "Threshold", rule.Threshold, 1, AlertRules.MaximumThreshold, problems);
        var window = Number(section, "WindowMinutes", rule.WindowMinutes, 1, AlertRules.MaximumWindowMinutes, problems);
        var cooldown = Number(section, "CooldownMinutes", rule.CooldownMinutes, 0, AlertRules.MaximumCooldownMinutes, problems);
        var above = rule.Above is null ? (long?)null : Long(section, "Above", rule.Above.Value, 1, AlertRules.MaximumAbove, problems);
        var severity = section["Severity"] ?? rule.Severity;
        if (!AlertSeverity.All.Contains(severity, StringComparer.Ordinal))
        {
            problems.Add($"{Section}:{rule.Name}:Severity is '{severity}', not one of {string.Join(", ", AlertSeverity.All)}.");
        }

        problems.AddRange(UnknownKeys(configuration, rule.Name).Select(key => $"{Section}:{rule.Name}:{key} is no value of a rule; the values are {string.Join(", ", Keys)}."));
        problems.AddRange(rule.Matches.Select(m => m.EventType).Distinct(StringComparer.Ordinal).Where(type => !declaredTypes.Contains(type))
            .Select(type => $"It counts {type}, which no module of this host declares."));
        if (problems.Count > 0)
        {
            return Error.Validation(MonitoringErrors.RuleValueInvalid, string.Join(" ", problems));
        }

        var operatorRule = rule with { Threshold = threshold, WindowMinutes = window, CooldownMinutes = cooldown, Above = above, Severity = severity };

        // Stricter only: a lower threshold, a longer window, a lower quantity. A looser value, stored before the operator
        // tightened theirs, gives way to the operator's.
        var effectiveThreshold = operatorRule.ThresholdEditable && tenant?.Threshold is { } t ? Math.Clamp(t, 1, threshold) : threshold;
        var effectiveWindow = operatorRule.ThresholdEditable && tenant?.WindowMinutes is { } w ? Math.Clamp(w, window, AlertRules.MaximumWindowMinutes) : window;
        var effectiveAbove = operatorRule.QuantityEditable && tenant?.Above is { } a && above is { } limit ? Math.Clamp(a, 1, limit) : above;
        return new ResolvedRule(rule, operatorRule, effectiveThreshold, effectiveWindow, effectiveAbove);
    }

    /// <summary>A tenant's values checked against the operator's (ADR-0056): stricter or the same, within the bounds; null for each that holds.</summary>
    public static IReadOnlyList<FieldError> Check(AlertRule operatorRule, TenantRuleValues values)
    {
        ArgumentNullException.ThrowIfNull(operatorRule);
        ArgumentNullException.ThrowIfNull(values);
        var errors = new List<FieldError>();
        if (!operatorRule.ThresholdEditable && (values.Threshold is not null || values.WindowMinutes is not null))
        {
            errors.Add(new FieldError("threshold", "fixed", "This rule raises an alert for every event, so its threshold and window cannot change."));
        }
        else
        {
            if (values.Threshold is { } threshold && (threshold < 1 || threshold > operatorRule.Threshold))
            {
                errors.Add(new FieldError("threshold", "looser", $"Choose a threshold from 1 to {operatorRule.Threshold.ToString(CultureInfo.InvariantCulture)}: higher would be looser than your platform operator's."));
            }

            if (values.WindowMinutes is { } window && (window < operatorRule.WindowMinutes || window > AlertRules.MaximumWindowMinutes))
            {
                errors.Add(new FieldError("window_minutes", "looser", $"Choose a window from {operatorRule.WindowMinutes.ToString(CultureInfo.InvariantCulture)} to {AlertRules.MaximumWindowMinutes.ToString(CultureInfo.InvariantCulture)} minutes: shorter would be looser than your platform operator's."));
            }
        }

        if (values.Above is { } above)
        {
            if (!operatorRule.QuantityEditable || operatorRule.Above is not { } limit)
            {
                errors.Add(new FieldError("above", "fixed", "This rule counts events, not records, so it has no limit of records to change."));
            }
            else if (above < 1 || above > limit)
            {
                errors.Add(new FieldError("above", "looser", $"Choose a limit from 1 to {limit.ToString(CultureInfo.InvariantCulture)} records: higher would be looser than your platform operator's."));
            }
        }

        return errors;
    }

    /// <summary>The names under <c>Monitoring:Rules</c> that no rule of the scope has: a misspelt rule is a failing one, not a silent default.</summary>
    public static IReadOnlyList<string> UnknownNames(IConfiguration configuration, MonitoringScope scope)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var known = AlertRules.All.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        return [.. configuration.GetSection(Section).GetChildren().Select(c => c.Key).Where(name => !known.Contains(name)).Order(StringComparer.Ordinal)];
    }

    /// <summary>The keys under a rule's section that are no value of a rule, such as a misspelt Threshold.</summary>
    public static IReadOnlyList<string> UnknownKeys(IConfiguration configuration, string rule)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return [.. configuration.GetSection($"{Section}:{rule}").GetChildren().Select(c => c.Key).Where(key => !Keys.Contains(key, StringComparer.Ordinal))];
    }

    private static int Number(IConfigurationSection section, string key, int fallback, int minimum, int maximum, List<string> problems)
    {
        var text = section[key];
        if (text is null)
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
        {
            problems.Add($"{section.Path}:{key} is '{text}', not a whole number from {minimum.ToString(CultureInfo.InvariantCulture)} to {maximum.ToString(CultureInfo.InvariantCulture)}.");
            return fallback;
        }

        return value;
    }

    private static long Long(IConfigurationSection section, string key, long fallback, long minimum, long maximum, List<string> problems)
    {
        var text = section[key];
        if (text is null)
        {
            return fallback;
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
        {
            problems.Add($"{section.Path}:{key} is '{text}', not a whole number from {minimum.ToString(CultureInfo.InvariantCulture)} to {maximum.ToString(CultureInfo.InvariantCulture)}.");
            return fallback;
        }

        return value;
    }
}
