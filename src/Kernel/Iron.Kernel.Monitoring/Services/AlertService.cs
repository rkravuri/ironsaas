using System.Globalization;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Monitoring.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Iron.Kernel.Monitoring.Services;

/// <summary>A rule as the rules page shows it: the operator's values, the tenant's, what runs, its bounds and its state.</summary>
public sealed record RuleView(
    AlertRule Rule,
    AlertRule? Operator,
    TenantRuleValues? Tenant,
    AlertRule? Effective,
    string State,
    string? Reason,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? FailingSince);

/// <summary>
/// What people do with alerts and rules (ADR-0056): acknowledge and resolve an alert, each audited, and make a rule
/// stricter than the operator's values or return it to them, audited as a change in a critical area, which the rules
/// themselves count.
/// </summary>
public sealed class AlertService(
    MonitoringDbContext context,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    IConfiguration configuration,
    AuditEventNames names,
    MonitoringPlace place,
    ICurrentPrincipal actor,
    TimeProvider time)
{
    public async Task<Result<Alert>> AcknowledgeAsync(Guid id, string? note, CancellationToken cancellationToken)
    {
        var alert = await context.Alerts.SingleOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
        if (Refusal(alert) is { } refused)
        {
            return refused;
        }

        if (alert!.State != AlertStates.Open)
        {
            return alert;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        alert.Acknowledge(actor.Id, time.GetUtcNow(), string.IsNullOrWhiteSpace(note) ? null : note.Trim());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(MonitoringAuditEvents.AlertAcknowledged, AuditOutcome.Success, actor.Id, alert.Rule, Details(alert), AlertLinks.RecordType, alert.Id, alert.Note), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return alert;
    }

    public async Task<Result<Alert>> ResolveAsync(Guid id, string? note, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return Error.Validation(MonitoringErrors.NoteRequired, "Say what you found and did, for the audit trail.", new FieldError("note", "required", "Say what you found and did."));
        }

        var alert = await context.Alerts.SingleOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
        if (Refusal(alert) is { } refused)
        {
            return refused;
        }

        if (alert!.State == AlertStates.Resolved)
        {
            return Error.Conflict(MonitoringErrors.AlertResolved, "The alert is resolved already.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        alert.Resolve(actor.Id, time.GetUtcNow(), note.Trim());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(MonitoringAuditEvents.AlertResolved, AuditOutcome.Success, actor.Id, alert.Rule, Details(alert), AlertLinks.RecordType, alert.Id, alert.Note), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return alert;
    }

    /// <summary>Every rule of this host, with its state, as the rules page lists them.</summary>
    public async Task<IReadOnlyList<RuleView>> RulesAsync(CancellationToken cancellationToken)
    {
        var declared = names.All.Select(t => t.Code).ToHashSet(StringComparer.Ordinal);
        var values = await context.RuleValues.AsNoTracking().ToDictionaryAsync(v => v.Rule, v => new TenantRuleValues(v.Threshold, v.WindowMinutes, v.Above), StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var states = await context.RuleStates.AsNoTracking().ToDictionaryAsync(s => s.Rule, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        return [.. AlertRules.For(place.Scope).Select(rule =>
        {
            var tenant = values.GetValueOrDefault(rule.Name);
            var resolved = AlertRuleResolver.Resolve(rule, configuration, tenant, declared);
            var state = states.GetValueOrDefault(rule.Name);

            // A definition that cannot run is failing now, whatever the last pass said.
            var failing = resolved.IsFailure ? resolved.Error.Message : state?.State == AlertRuleState.Failing ? state.Reason : null;
            return new RuleView(
                rule,
                resolved.IsSuccess ? resolved.Value.Operator : null,
                tenant,
                resolved.IsSuccess ? resolved.Value.Effective : null,
                failing is null ? AlertRuleState.Running : AlertRuleState.Failing,
                failing,
                state?.LastRunAt,
                state?.FailingSince);
        })];
    }

    /// <summary>The evaluation's own state: when the rules last ran.</summary>
    public Task<AlertRuleState?> EvaluationAsync(CancellationToken cancellationToken) =>
        context.RuleStates.AsNoTracking().SingleOrDefaultAsync(s => s.Rule == RuleEvaluator.Evaluation, cancellationToken);

    /// <summary>
    /// The tenant's values for a rule: each stricter than the operator's or the same, or null to keep the operator's (TF-079,
    /// ADR-0056). A looser value is refused and names the bound.
    /// </summary>
    public async Task<Result<RuleView>> SetAsync(string name, TenantRuleValues values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        var rule = AlertRules.Find(place.Scope, name);
        if (rule is null)
        {
            return Error.NotFound(MonitoringErrors.RuleNotFound, $"There is no alert rule {name}.");
        }

        var declared = names.All.Select(t => t.Code).ToHashSet(StringComparer.Ordinal);
        var resolved = AlertRuleResolver.Resolve(rule, configuration, null, declared);
        if (resolved.IsFailure)
        {
            return Error.Conflict(MonitoringErrors.RuleValueInvalid, $"The rule cannot run as your platform operator set it, so it cannot be changed now: {resolved.Error.Message}");
        }

        var errors = AlertRuleResolver.Check(resolved.Value.Operator, values);
        if (errors.Count > 0)
        {
            return Error.Validation(MonitoringErrors.RuleValueLooser, "A tenant can make an alert rule stricter than its platform operator's values, never looser.", [.. errors]);
        }

        var stored = await context.RuleValues.SingleOrDefaultAsync(v => v.Rule == name, cancellationToken).ConfigureAwait(false);
        var before = stored is null ? new TenantRuleValues(null, null, null) : new TenantRuleValues(stored.Threshold, stored.WindowMinutes, stored.Above);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            stored = new AlertRuleValue(name);
            context.RuleValues.Add(stored);
        }

        stored.Set(values.Threshold, values.WindowMinutes, values.Above);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["threshold_before"] = Text(before.Threshold),
            ["threshold_after"] = Text(values.Threshold),
            ["window_minutes_before"] = Text(before.WindowMinutes),
            ["window_minutes_after"] = Text(values.WindowMinutes),
            ["above_before"] = Text(before.Above),
            ["above_after"] = Text(values.Above),
        };
        await audit.WriteAsync(new AuditEvent(MonitoringAuditEvents.RuleChanged, AuditOutcome.Success, actor.Id, name, details, "alert_rule"), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await RulesAsync(cancellationToken).ConfigureAwait(false)).Single(view => view.Rule.Name == name);
    }

    /// <summary>A copy is the unit's to acknowledge and resolve, where its events are.</summary>
    private static Error? Refusal(Alert? alert) =>
        alert is null ? Error.NotFound(MonitoringErrors.AlertNotFound, "There is no such alert.")
        : alert.Origin == Alert.Copy ? Error.Conflict(MonitoringErrors.AlertIsACopy, $"This alert is the tenant {alert.Tenant}'s: it is acknowledged and resolved in its unit, where its events are.")
        : null;

    private static Dictionary<string, string> Details(Alert alert) => new(StringComparer.Ordinal)
    {
        ["state"] = alert.State,
        ["group_kind"] = alert.GroupKind,
        ["group"] = alert.GroupValue,
        ["count"] = alert.Count.ToString(CultureInfo.InvariantCulture),
    };

    private static string Text(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "default";
}
