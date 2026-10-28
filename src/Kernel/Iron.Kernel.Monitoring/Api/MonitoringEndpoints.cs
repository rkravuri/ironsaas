using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Monitoring.Data;
using Iron.Kernel.Monitoring.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Monitoring.Api;

/// <summary>A security alert as the console shows it; a person in the group is named when this unit knows them.</summary>
public sealed record AlertResponse(
    Guid Id,
    string Tenant,
    string Origin,
    string Rule,
    string Title,
    string Kind,
    string Severity,
    string State,
    string GroupKind,
    string GroupValue,
    string? GroupName,
    long Count,
    int Threshold,
    int WindowMinutes,
    DateTimeOffset? FirstEventAt,
    DateTimeOffset? LastEventAt,
    DateTimeOffset RaisedAt,
    string? Reason,
    string? Link,
    string? Search,
    string? Runbook,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgedBy,
    DateTimeOffset? ResolvedAt,
    string? ResolvedBy,
    string? Note);

public sealed record AlertNoteRequest(string? Note);

public sealed class AlertNoteRequestValidator : AbstractValidator<AlertNoteRequest>
{
    public AlertNoteRequestValidator() => RuleFor(r => r.Note).MaximumLength(Alert.TextMaxLength);
}

/// <summary>A rule's values at one level: the threshold, the window, the cool-down, the limit of records, the severity.</summary>
public sealed record RuleValuesResponse(int Threshold, int WindowMinutes, int CooldownMinutes, long? Above, string Severity);

/// <summary>What a tenant may set, and between which values: always stricter than the operator's.</summary>
public sealed record RuleBoundsResponse(bool ThresholdEditable, int ThresholdMinimum, int ThresholdMaximum, int WindowMinimum, int WindowMaximum, bool AboveEditable, long? AboveMinimum, long? AboveMaximum);

public sealed record TenantValuesResponse(int? Threshold, int? WindowMinutes, long? Above);

/// <summary>A rule as the rules page shows it: what it counts, its values, what the tenant chose, and whether it runs.</summary>
public sealed record AlertRuleResponse(
    string Name,
    string Title,
    IReadOnlyList<string> EventTypes,
    string GroupBy,
    bool AtOnce,
    string Runbook,
    RuleValuesResponse? Operator,
    TenantValuesResponse Tenant,
    RuleValuesResponse? Effective,
    RuleBoundsResponse? Bounds,
    string State,
    string? Reason,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? FailingSince);

/// <summary>The rules and when they last ran together.</summary>
public sealed record AlertRulesResponse(IReadOnlyList<AlertRuleResponse> Data, DateTimeOffset? EvaluatedAt);

/// <summary>A tenant's values for a rule; each null returns it to the operator's.</summary>
public sealed record AlertRuleValuesRequest(int? Threshold, int? WindowMinutes, long? Above);

public sealed class AlertRuleValuesRequestValidator : AbstractValidator<AlertRuleValuesRequest>
{
    public AlertRuleValuesRequestValidator()
    {
        RuleFor(r => r.Threshold).InclusiveBetween(1, AlertRules.MaximumThreshold);
        RuleFor(r => r.WindowMinutes).InclusiveBetween(1, AlertRules.MaximumWindowMinutes);
        RuleFor(r => r.Above).InclusiveBetween(1, AlertRules.MaximumAbove);
    }
}

/// <summary>
/// The security alerts and the alert rules (TF-079, ADR-0056): the alerts listed with their filters, acknowledged and
/// resolved; the rules with their thresholds and state, a tenant's stricter values set or cleared.
/// </summary>
internal static class MonitoringEndpoints
{
    public static readonly ListFields<Alert> Fields = new ListFields<Alert>()
        .Add("id", a => a.Id, FieldUse.Select | FieldUse.Filter)
        .Add("tenant", a => a.Tenant, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("origin", a => a.Origin, FieldUse.Select | FieldUse.Filter)
        .Add("rule", a => a.Rule, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("kind", a => a.Kind, FieldUse.Select | FieldUse.Filter)
        .Add("severity", a => a.Severity, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("state", a => a.State, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("group_kind", a => a.GroupKind, FieldUse.Select | FieldUse.Filter)
        .Add("group_value", a => a.GroupValue, FieldUse.Select | FieldUse.Filter)
        .Add("source_id", a => a.SourceId, FieldUse.Select | FieldUse.Filter)
        .Add("count", a => a.Count, FieldUse.Select | FieldUse.Sort)
        .Add("raised_at", a => a.RaisedAt, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .SortByDefault("raised_at", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var alerts = endpoints.MapApiGroup("alerts");
        alerts.MapGet("/", ListAsync).RequirePermission(MonitoringPermissions.AlertsRead).Produces<ListResponse<AlertResponse>>(StatusCodes.Status200OK).WithName("ListSecurityAlerts").WithSummary("The security alerts, newest first, by state, rule, severity and time.");
        alerts.MapGet("/{id:guid}", GetAsync).RequirePermission(MonitoringPermissions.AlertsRead).Produces<AlertResponse>(StatusCodes.Status200OK).WithName("GetSecurityAlert").WithSummary("One security alert, with the links to its events and its runbook.");
        alerts.MapPost("/{id:guid}/acknowledge", AcknowledgeAsync).RequirePermission(MonitoringPermissions.AlertsManage).Validate<AlertNoteRequest>().Produces<AlertResponse>(StatusCodes.Status200OK).WithName("AcknowledgeSecurityAlert").WithSummary("Say you are on the alert: the operator's page for it stops.");
        alerts.MapPost("/{id:guid}/resolve", ResolveAsync).RequirePermission(MonitoringPermissions.AlertsManage).Validate<AlertNoteRequest>().Produces<AlertResponse>(StatusCodes.Status200OK).WithName("ResolveSecurityAlert").WithSummary("Resolve the alert with a note of what you found and did.");

        var rules = endpoints.MapApiGroup("alert-rules");
        rules.MapGet("/", RulesAsync).RequirePermission(MonitoringPermissions.AlertsRead).Produces<AlertRulesResponse>(StatusCodes.Status200OK).WithName("ListAlertRules").WithSummary("Every alert rule: what it counts, its values, the tenant's, and whether it runs or fails.");
        rules.MapPut("/{name}", SetAsync).RequirePermission(MonitoringPermissions.RulesManage).Validate<AlertRuleValuesRequest>().Produces<AlertRuleResponse>(StatusCodes.Status200OK).WithName("SetAlertRuleValues").WithSummary("Make a rule stricter than the operator's values, or return a value to theirs with null.");
    }

    private static async Task<IResult> ListAsync(HttpContext http, MonitoringDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.Alerts.AsNoTracking().ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        var names = await NamesAsync(http, page.Items, cancellationToken);
        return ApiResults.List(page.Map(alert => Response(alert, names)), http, query);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, MonitoringDbContext context, CancellationToken cancellationToken)
    {
        var alert = await context.Alerts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (alert is null)
        {
            return ApiResults.Problem(Error.NotFound(MonitoringErrors.AlertNotFound, "There is no such alert."), http);
        }

        return TypedResults.Ok(Response(alert, await NamesAsync(http, [alert], cancellationToken)));
    }

    private static async Task<IResult> AcknowledgeAsync(Guid id, AlertNoteRequest request, HttpContext http, AlertService alerts, CancellationToken cancellationToken)
    {
        var result = await alerts.AcknowledgeAsync(id, request.Note, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok(Response(result.Value, await NamesAsync(http, [result.Value], cancellationToken)));
    }

    private static async Task<IResult> ResolveAsync(Guid id, AlertNoteRequest request, HttpContext http, AlertService alerts, CancellationToken cancellationToken)
    {
        var result = await alerts.ResolveAsync(id, request.Note, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok(Response(result.Value, await NamesAsync(http, [result.Value], cancellationToken)));
    }

    private static async Task<IResult> RulesAsync(AlertService alerts, CancellationToken cancellationToken)
    {
        var views = await alerts.RulesAsync(cancellationToken);
        var evaluation = await alerts.EvaluationAsync(cancellationToken);
        return TypedResults.Ok(new AlertRulesResponse([.. views.Select(Response)], evaluation?.LastRunAt));
    }

    private static async Task<IResult> SetAsync(string name, AlertRuleValuesRequest request, HttpContext http, AlertService alerts, CancellationToken cancellationToken)
    {
        var result = await alerts.SetAsync(name, new TenantRuleValues(request.Threshold, request.WindowMinutes, request.Above), cancellationToken);
        return ApiResults.From(result, http, view => TypedResults.Ok(Response(view)));
    }

    /// <summary>Everyone the alerts name, by name: the group's person, and who acknowledged and resolved.</summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(HttpContext http, IReadOnlyList<Alert> alerts, CancellationToken cancellationToken)
    {
        if (http.RequestServices.GetService<IPrincipals>() is not { } principals)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = alerts.Where(a => a.Origin == Alert.Local && a.GroupKind == "principal" && Guid.TryParse(a.GroupValue, out _)).Select(a => Guid.Parse(a.GroupValue))
            .Concat(alerts.Where(a => a.AcknowledgedById is not null).Select(a => a.AcknowledgedById!.Value))
            .Concat(alerts.Where(a => a.ResolvedById is not null).Select(a => a.ResolvedById!.Value))
            .Distinct()
            .ToList();
        return ids.Count == 0 ? new Dictionary<Guid, string>() : (await principals.FindAsync(ids, cancellationToken)).ToDictionary(p => p.Key, p => p.Value.DisplayName);
    }

    private static AlertResponse Response(Alert alert, IReadOnlyDictionary<Guid, string> names)
    {
        string? Name(Guid? id) => id is { } value && names.TryGetValue(value, out var name) ? name : null;
        var group = alert.GroupKind == "principal" && Guid.TryParse(alert.GroupValue, out var person) ? Name(person) : null;
        return new AlertResponse(
            alert.Id, alert.Tenant, alert.Origin, alert.Rule, alert.Title, alert.Kind, alert.Severity, alert.State, alert.GroupKind, alert.GroupValue, group,
            alert.Count, alert.Threshold, alert.WindowMinutes, alert.FirstEventAt, alert.LastEventAt, alert.RaisedAt, alert.Reason, alert.Link, alert.Search, alert.Runbook,
            alert.AcknowledgedAt, Name(alert.AcknowledgedById), alert.ResolvedAt, Name(alert.ResolvedById), alert.Note);
    }

    private static AlertRuleResponse Response(RuleView view)
    {
        var rule = view.Rule;
        var bounds = view.Operator is not { } op
            ? null
            : new RuleBoundsResponse(op.ThresholdEditable, 1, op.Threshold, op.WindowMinutes, AlertRules.MaximumWindowMinutes, op.QuantityEditable, op.Above is null ? null : 1, op.Above);
        return new AlertRuleResponse(
            rule.Name,
            rule.Title,
            [.. rule.Matches.Select(m => m.EventType).Distinct(StringComparer.Ordinal)],
            AlertLinks.GroupKind(rule.GroupBy),
            rule.AtOnce,
            rule.Runbook,
            Values(view.Operator),
            new TenantValuesResponse(view.Tenant?.Threshold, view.Tenant?.WindowMinutes, view.Tenant?.Above),
            Values(view.Effective),
            bounds,
            view.State,
            view.Reason,
            view.LastRunAt,
            view.FailingSince);
    }

    private static RuleValuesResponse? Values(AlertRule? rule) =>
        rule is null ? null : new RuleValuesResponse(rule.Threshold, rule.WindowMinutes, rule.CooldownMinutes, rule.Above, rule.Severity);
}
