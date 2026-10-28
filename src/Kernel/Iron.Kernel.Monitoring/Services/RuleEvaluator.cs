using System.Diagnostics.Metrics;
using System.Globalization;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Monitoring.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Monitoring.Services;

/// <summary>The Monitoring module's settings, from the <c>Monitoring</c> section (ADR-0056).</summary>
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>How often the rules run: every minute (TF-079).</summary>
    public int EvaluationSeconds { get; set; } = 60;

    /// <summary>A window ends this long before now, so a transaction still committing is counted at the next pass, not missed.</summary>
    public int SettleSeconds { get; set; } = 5;

    /// <summary>A pass this long after the last one reports the gap as a failing evaluation: longer than a deployment takes.</summary>
    public int LateAfterMinutes { get; set; } = 10;

    /// <summary>How far back a rule that raises an alert for each event catches up after a gap.</summary>
    public int CatchUpHours { get; set; } = 24;

    /// <summary>How often a unit sends the control plane its changed alerts.</summary>
    public int CopySeconds { get; set; } = 15;

    public AlertmanagerOptions Alertmanager { get; set; } = new();
}

/// <summary>The operator's alerting (ADR-0056): the Alertmanager the control plane sends every alert to.</summary>
public sealed class AlertmanagerOptions
{
    /// <summary>Its address, such as <c>http://alertmanager:9093</c>; empty, the alerts stay in the control plane.</summary>
    public string? Address { get; set; }

    public int Seconds { get; set; } = 30;
}

/// <summary>Where this host's module runs, as the host said when it listed the module.</summary>
public sealed record MonitoringPlace(MonitoringScope Scope);

public static class MonitoringMetrics
{
    private static readonly Meter Meter = new("Iron.Monitoring");

    public static readonly Counter<long> AlertsRaised = Meter.CreateCounter<long>("monitoring.alerts_raised", unit: "{alert}", description: "Security alerts raised, by rule and kind.");

    public static readonly Counter<long> RulesFailing = Meter.CreateCounter<long>("monitoring.rules_failing", unit: "{rule}", description: "Rules that turned failing, by rule (TF-079 AC4).");

    public static readonly Counter<long> CopyFailures = Meter.CreateCounter<long>("monitoring.copy_failures", unit: "{attempt}", description: "Sends of a unit's alerts to the control plane that failed; they are tried again.");

    public static readonly Counter<long> AlertmanagerFailures = Meter.CreateCounter<long>("monitoring.alertmanager_failures", unit: "{attempt}", description: "Sends to the operator's alerting that failed; they are tried again.");
}

/// <summary>What one pass did: the rules it ran, the alerts it raised, the rules failing.</summary>
public sealed record EvaluationPass(int Rules, int Raised, int Absorbed, int Failing);

/// <summary>
/// One pass of the security alert rules (TF-079, ADR-0056), every minute in the unit's worker and in the control plane.
/// Each rule counts its audit events over its window, by group; a group at or above the threshold raises an alert, unless
/// an alert of the same rule and group was raised within the cool-down, which then counts the burst on. An event is never
/// counted by two alerts of a rule and group: a later count starts after the last event the previous alert saw. A rule
/// that cannot run, for its definition or its evaluation, is failing and raises an alert of its own, never a silence
/// (AC4); so does a pass that finds the rules did not run for a while.
/// </summary>
public sealed class RuleEvaluator(
    MonitoringDbContext context,
    IAuditCounts counts,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    IConfiguration configuration,
    AuditEventNames names,
    MonitoringOptions options,
    MonitoringPlace place,
    TenantContext tenant,
    TimeProvider time,
    ILogger<RuleEvaluator> logger)
{
    /// <summary>One pass at a time in a database, whichever process runs it.</summary>
    public const long LockKey = 7_900_790_000_079;

    /// <summary>The state that records the passes themselves, so a gap between them is seen.</summary>
    public const string Evaluation = "evaluation";

    public async Task<EvaluationPass> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var until = now - TimeSpan.FromSeconds(Math.Max(0, options.SettleSeconds));
        var declared = names.All.Select(t => t.Code).ToHashSet(StringComparer.Ordinal);
        var values = await context.RuleValues.AsNoTracking().ToDictionaryAsync(v => v.Rule, v => new TenantRuleValues(v.Threshold, v.WindowMinutes, v.Above), StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var pass = new EvaluationPass(0, 0, 0, 0);

        pass = await GuardAsync(Evaluation, pass, () => PassesAsync(now, until, cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var rule in AlertRules.For(place.Scope))
        {
            pass = await GuardAsync(rule.Name, pass, () => EvaluateAsync(rule, values.GetValueOrDefault(rule.Name), declared, now, until, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        foreach (var name in AlertRuleResolver.UnknownNames(configuration, place.Scope))
        {
            pass = await GuardAsync(name, pass, () => InTransactionAsync(async () =>
            {
                var state = await StateAsync(name, cancellationToken).ConfigureAwait(false);
                return await FailAsync(state, $"{AlertRuleResolver.Section}:{name} names no rule. The rules are {string.Join(", ", AlertRules.For(place.Scope).Select(r => r.Name))}.", now, cancellationToken).ConfigureAwait(false);
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        return pass;
    }

    /// <summary>A rule's evaluation that throws is that rule failing, with the reason; the other rules still run.</summary>
    private async Task<EvaluationPass> GuardAsync(string rule, EvaluationPass pass, Func<Task<Outcome>> run, CancellationToken cancellationToken)
    {
        Outcome outcome;
        try
        {
            outcome = await run().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The alert rule {Rule} could not be evaluated.", rule);
            context.ChangeTracker.Clear();
            outcome = await InTransactionAsync(async () =>
            {
                var state = await StateAsync(rule, cancellationToken).ConfigureAwait(false);
                return await FailAsync(state, $"Its evaluation failed: {exception.Message}", time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }

        return new EvaluationPass(pass.Rules + (rule == Evaluation ? 0 : 1), pass.Raised + outcome.Raised, pass.Absorbed + outcome.Absorbed, pass.Failing + (outcome.Failing ? 1 : 0));
    }

    private Task<Outcome> PassesAsync(DateTimeOffset now, DateTimeOffset until, CancellationToken cancellationToken) => InTransactionAsync(async () =>
    {
        var state = await StateAsync(Evaluation, cancellationToken).ConfigureAwait(false);
        var raised = 0;
        if (state.LastRunAt is { } last && now - last > TimeSpan.FromMinutes(options.LateAfterMinutes))
        {
            var reason = $"The rules did not run from {Format(last)} to {Format(now)}. The rules that raise an alert for each event looked back over the gap; the rules that count over a window did not.";
            await RaiseFailingAsync(Evaluation, "The alert rules did not run for a while", reason, now, cancellationToken).ConfigureAwait(false);
            raised = 1;
        }

        state.Ran(now, until);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new Outcome(raised, 0, false);
    }, cancellationToken);

    private Task<Outcome> EvaluateAsync(AlertRule rule, TenantRuleValues? values, IReadOnlySet<string> declared, DateTimeOffset now, DateTimeOffset until, CancellationToken cancellationToken) => InTransactionAsync(async () =>
    {
        var state = await StateAsync(rule.Name, cancellationToken).ConfigureAwait(false);
        var resolved = AlertRuleResolver.Resolve(rule, configuration, values, declared);
        if (resolved.IsFailure)
        {
            return await FailAsync(state, resolved.Error.Message, now, cancellationToken).ConfigureAwait(false);
        }

        var effective = resolved.Value.Effective;
        var after = until - TimeSpan.FromMinutes(effective.WindowMinutes);
        if (effective.AtOnce && state.CountedTo is { } counted && counted < after)
        {
            // Each event matters: after a gap, look back to where the last pass counted, within reason.
            var earliest = until - TimeSpan.FromHours(Math.Max(1, options.CatchUpHours));
            after = counted > earliest ? counted : earliest;
        }

        var raised = 0;
        var absorbed = 0;
        foreach (var group in await counts.CountAsync(Query(effective, after, until), cancellationToken).ConfigureAwait(false))
        {
            var previous = await context.Alerts
                .Where(a => a.Origin == Alert.Local && a.Rule == rule.Name && a.Kind == AlertKinds.Threshold && a.GroupValue == group.Group)
                .OrderByDescending(a => a.RaisedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var found = group;
            if (previous?.LastEventAt is { } seen && seen > after)
            {
                // The previous alert saw the window's start: only what came after its last event counts now.
                var since = await counts.CountAsync(Query(effective, seen, until) with { Group = group.Group }, cancellationToken).ConfigureAwait(false);
                if (since.Count == 0)
                {
                    continue;
                }

                found = since[0];
            }

            if (previous is not null && previous.State != AlertStates.Resolved && now - previous.RaisedAt < TimeSpan.FromMinutes(effective.CooldownMinutes))
            {
                previous.Absorb(found.Measure, found.Last);
                absorbed++;
                continue;
            }

            if (found.Measure >= effective.Threshold)
            {
                await RaiseAsync(effective, found, now, cancellationToken).ConfigureAwait(false);
                raised++;
            }
        }

        if (state.State == AlertRuleState.Failing)
        {
            await audit.WriteAsync(new AuditEvent(MonitoringAuditEvents.RuleRecovered, AuditOutcome.Success, SystemPrincipal.SystemId, rule.Name, new Dictionary<string, string>(StringComparer.Ordinal) { ["was"] = state.Reason ?? string.Empty }), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("The alert rule {Rule} runs again.", rule.Name);
        }

        state.Ran(now, until);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new Outcome(raised, absorbed, false);
    }, cancellationToken);

    /// <summary>The query a rule's count makes over a window.</summary>
    public static AuditCountQuery Query(AlertRule rule, DateTimeOffset after, DateTimeOffset until)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new AuditCountQuery(rule.Matches, rule.GroupBy, after, until, rule.QuantityKey, rule.Above, rule.SumKey);
    }

    private async Task RaiseAsync(AlertRule rule, AuditGroupCount group, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var kind = AlertLinks.GroupKind(rule.GroupBy);
        var search = AlertLinks.Api(rule, group.Group, group.First, group.Last);

        // The control plane has no shell: its own alerts lead to the API's search of its trail.
        var link = place.Scope == MonitoringScope.Unit ? AlertLinks.Shell(rule, group.Group, group.First, group.Last) : search;
        var alert = new Alert(
            tenant.Id.Value, rule.Name, rule.Title, AlertKinds.Threshold, rule.Severity, kind, group.Group, group.Measure, rule.Threshold, rule.WindowMinutes,
            group.First, group.Last, now, null, link, search, rule.Runbook);
        context.Alerts.Add(alert);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alert_id"] = alert.Id.ToString("D"),
            ["group_kind"] = kind,
            ["group"] = group.Group,
            ["count"] = group.Measure.ToString(CultureInfo.InvariantCulture),
            ["threshold"] = rule.Threshold.ToString(CultureInfo.InvariantCulture),
            ["window_minutes"] = rule.WindowMinutes.ToString(CultureInfo.InvariantCulture),
            ["severity"] = rule.Severity,
        };
        await audit.WriteAsync(new AuditEvent(MonitoringAuditEvents.AlertRaised, AuditOutcome.Success, SystemPrincipal.SystemId, rule.Name, details, AlertLinks.RecordType, alert.Id, Severity: rule.Severity == AlertSeverity.Normal ? AuditSeverity.Normal : AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        MonitoringMetrics.AlertsRaised.Add(1, new KeyValuePair<string, object?>("rule", rule.Name), new KeyValuePair<string, object?>("kind", AlertKinds.Threshold));
        logger.LogError("ALERT: {Title} ({Rule}): {Count} for {GroupKind} {Group} within {Window} minutes, at or above {Threshold}; alert {AlertId}.", rule.Title, rule.Name, group.Measure, kind, group.Group, rule.WindowMinutes, rule.Threshold, alert.Id);
    }

    /// <summary>The rule turns failing: its state says why, and the first time an alert of its own says so too.</summary>
    private async Task<Outcome> FailAsync(AlertRuleState state, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var turned = state.State != AlertRuleState.Failing;
        state.Fail(now, reason);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (turned)
        {
            var title = AlertRules.All.FirstOrDefault(r => r.Name == state.Rule)?.Title is { } named ? $"The rule \"{named}\" is failing" : "An alert rule is failing";
            await RaiseFailingAsync(state.Rule, title, reason, now, cancellationToken).ConfigureAwait(false);
            MonitoringMetrics.RulesFailing.Add(1, new KeyValuePair<string, object?>("rule", state.Rule));
        }

        return new Outcome(turned ? 1 : 0, 0, true);
    }

    private async Task RaiseFailingAsync(string rule, string title, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var alert = new Alert(tenant.Id.Value, rule, title, AlertKinds.Failing, AlertSeverity.High, AlertLinks.RuleGroup, rule, 0, 0, 0, null, null, now, reason, AlertLinks.RulePage(rule), null, AlertRules.FailingRunbook);
        context.Alerts.Add(alert);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["alert_id"] = alert.Id.ToString("D"), ["reason"] = reason };
        await audit.WriteAsync(new AuditEvent(MonitoringAuditEvents.RuleFailing, AuditOutcome.Failure, SystemPrincipal.SystemId, rule, details, AlertLinks.RecordType, alert.Id, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        MonitoringMetrics.AlertsRaised.Add(1, new KeyValuePair<string, object?>("rule", rule), new KeyValuePair<string, object?>("kind", AlertKinds.Failing));
        logger.LogError("ALERT: the alert rule {Rule} is failing: {Reason} Alert {AlertId}.", rule, reason, alert.Id);
    }

    private async Task<AlertRuleState> StateAsync(string rule, CancellationToken cancellationToken)
    {
        var state = await context.RuleStates.SingleOrDefaultAsync(s => s.Rule == rule, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            state = new AlertRuleState(rule);
            context.RuleStates.Add(state);
        }

        return state;
    }

    private async Task<Outcome> InTransactionAsync(Func<Task<Outcome>> work, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("select pg_advisory_xact_lock({0})", [LockKey], cancellationToken).ConfigureAwait(false);
        var outcome = await work().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private static string Format(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private sealed record Outcome(int Raised, int Absorbed, bool Failing);
}
