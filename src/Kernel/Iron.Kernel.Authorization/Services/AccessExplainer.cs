using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Policies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Authorization.Services;

/// <summary>The checks an explanation lists, in the order a request meets them (ADR-0039).</summary>
public static class ExplainChecks
{
    public const string Principal = "principal";
    public const string Entitlement = "entitlement";
    public const string Flags = "flags";
    public const string Permission = "permission";
    public const string RecordPolicy = "record_policy";
}

/// <summary>What became of a check: passed, failed (the first failure decides), not reached after a failure, or nothing to check.</summary>
public static class ExplainResults
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string NotReached = "not_reached";
    public const string NotApplicable = "not_applicable";
}

/// <summary>A route value an action needs: a unit (its permission is checked there), a record (and its policy), or a plain value.</summary>
public sealed record ActionRouteValue(string Name, string Kind, string? RecordType);

/// <summary>An endpoint as an explanation names it: its name, its summary in words, its module, and what it needs.</summary>
public sealed record ExplainableAction(string Name, string Summary, string Module, string Method, string Route, string? Permission, IReadOnlyList<ActionRouteValue> RouteValues);

public sealed record ExplainedUnit(Guid Id, string Name);

/// <summary>An assignment that granted the permission: the role and the scope (TF-039 AC2).</summary>
public sealed record ExplainedGrant(Guid AssignmentId, Guid RoleId, string RoleCode, string RoleName, string ScopeKind, ExplainedUnit? Unit, string? RecordType, Guid? RecordId);

/// <summary>
/// One check, explained: its result, its reason code, the status a failure answers, the error code the API gives, the
/// facts behind it (the licence, the flag, the permission, the scope) and, for a permission granted, the assignments.
/// </summary>
public sealed record ExplainedCheck(string Check, string Result, string Reason, int? Status, string? ErrorCode, string Message, IReadOnlyDictionary<string, string> Facts, IReadOnlyList<ExplainedGrant> GrantedBy)
{
    public static ExplainedCheck NotReached(string check) => new(check, ExplainResults.NotReached, ExplainResults.NotReached, null, null, "Not reached: an earlier check refused the request.", Empty, []);

    internal static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record ExplainedPrincipal(Guid Id, string Name, PrincipalKind Type, string Status, ExplainedUnit? HomeUnit);

/// <summary>
/// Why a person can or cannot perform an action (TF-039, ADR-0039): the checks in order, the outcome, and the status the
/// API would answer them. A denial's first failing check is the reason; an allowance names the granting role and scope.
/// </summary>
public sealed record AccessExplanationResult(ExplainedPrincipal Principal, ExplainableAction Action, ExplainedPrincipal? ActingFor, bool Allowed, int Status, IReadOnlyList<ExplainedCheck> Checks);

/// <summary>
/// Explains access by running the API's own checks for another person, in the order a request meets them: the
/// principal, the module's entitlement and features, the flags, the permission at its scope (as the delegator's while
/// acting), and the record policy of a record route (ADR-0039). Each check is the decision the filters act on, read
/// without its side effects, so the explanation cannot disagree with the API. Checks a handler makes beyond these
/// (a move's new parent, a merge's target) are not in it.
/// </summary>
public sealed class AccessExplainer(
    EndpointDataSource endpoints,
    IServiceProvider services,
    IPrincipals principals,
    IPermissionEvaluator evaluator,
    ActingScope acting,
    AuthorizationDbContext context)
{
    /// <summary>Every endpoint that needs a permission or a signed-in principal and has a name, by module then summary.</summary>
    public IReadOnlyList<ExplainableAction> Actions() =>
        [.. endpoints.Endpoints.OfType<RouteEndpoint>().Select(Describe).OfType<ExplainableAction>()
            .GroupBy(a => a.Name, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(a => a.Module, StringComparer.Ordinal).ThenBy(a => a.Summary, StringComparer.Ordinal)];

    public async Task<Result<AccessExplanationResult>> ExplainAsync(Guid principalId, string actionName, IReadOnlyDictionary<string, string> routeValues, Guid? actingForId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routeValues);
        var endpoint = Find(actionName);
        if (endpoint is null || Describe(endpoint) is not { } action)
        {
            return Error.NotFound(AuthorizationErrors.ExplainActionUnknown, $"There is no action '{actionName}' to explain. Choose one from the list of actions.");
        }

        var people = await principals.FindAsync(actingForId is { } delegatorId ? [principalId, delegatorId] : [principalId], cancellationToken).ConfigureAwait(false);
        if (!people.TryGetValue(principalId, out var principal))
        {
            return Error.NotFound(AuthorizationErrors.PrincipalNotFound, "There is no user or service account with this id in this tenant.");
        }

        PrincipalSummary? delegator = null;
        if (actingForId is { } id)
        {
            if (!people.TryGetValue(id, out delegator))
            {
                return Error.Validation(AuthorizationErrors.PrincipalNotFound, "There is no person with this id to act for.", new FieldError("acting_for", "not_found", "Choose a person of this tenant."));
            }

            acting.Assume(principalId, id);
        }

        string? Value(string name) => routeValues.TryGetValue(name, out var value) ? value : null;
        var metadata = endpoint.Metadata;
        var method = action.Method;
        var operation = $"{method} {action.Route}";
        var checks = new List<ExplainedCheck> { PrincipalCheck(principal) };
        if (checks[^1].Result != ExplainResults.Failed)
        {
            checks.Add(await EntitlementCheckAsync(metadata, method, principalId, operation, action.Route, cancellationToken).ConfigureAwait(false));
        }

        if (checks[^1].Result != ExplainResults.Failed)
        {
            checks.Add(await FlagsCheckAsync(metadata, principalId, operation, action.Route, cancellationToken).ConfigureAwait(false));
        }

        if (checks[^1].Result != ExplainResults.Failed)
        {
            checks.Add(await PermissionCheckAsync(metadata, principalId, operation, action.Route, Value, cancellationToken).ConfigureAwait(false));
        }

        if (checks[^1].Result != ExplainResults.Failed)
        {
            checks.Add(await RecordPolicyCheckAsync(metadata, principalId, Value, cancellationToken).ConfigureAwait(false));
        }

        string[] order = [ExplainChecks.Principal, ExplainChecks.Entitlement, ExplainChecks.Flags, ExplainChecks.Permission, ExplainChecks.RecordPolicy];
        checks.AddRange(order.Skip(checks.Count).Select(ExplainedCheck.NotReached));
        var failed = checks.FirstOrDefault(c => c.Result == ExplainResults.Failed);
        var units = await UnitsAsync([principal.HomeUnitId, delegator?.HomeUnitId], cancellationToken).ConfigureAwait(false);
        return new AccessExplanationResult(
            Person(principal, units),
            action,
            delegator is null ? null : Person(delegator, units),
            failed is null,
            failed?.Status ?? StatusCodes.Status200OK,
            checks);
    }

    private RouteEndpoint? Find(string actionName) =>
        endpoints.Endpoints.OfType<RouteEndpoint>().FirstOrDefault(e => string.Equals(e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName, actionName, StringComparison.Ordinal));

    private static ExplainableAction? Describe(RouteEndpoint endpoint)
    {
        var metadata = endpoint.Metadata;
        var name = metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        var permission = metadata.GetMetadata<PermissionMetadata>()?.Permission;
        if (name is null || (permission is null && metadata.GetMetadata<SignedInMetadata>() is null))
        {
            return null;
        }

        var scope = metadata.GetMetadata<ScopeMetadata>();
        var values = endpoint.RoutePattern.Parameters.Select(p => p.Name).Select(parameter => scope switch
        {
            UnitScopeMetadata unit when unit.RouteValue == parameter => new ActionRouteValue(parameter, "unit", null),
            RecordScopeMetadata record when record.RouteValue == parameter => new ActionRouteValue(parameter, "record", record.RecordType),
            _ => new ActionRouteValue(parameter, "value", null),
        }).ToList();
        var method = metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? HttpMethods.Get;
        return new ExplainableAction(
            name,
            metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary ?? name,
            metadata.GetMetadata<ModuleMetadata>()?.Name ?? "host",
            method,
            EndpointPermissionCheck.Route(endpoint),
            permission?.Name,
            values);
    }

    private static ExplainedCheck PrincipalCheck(PrincipalSummary principal)
    {
        var facts = Facts(("type", principal.Type == PrincipalKind.Service ? "service" : "user"), ("status", principal.Status));
        return principal.Status == PrincipalStatuses.Active
            ? new ExplainedCheck(ExplainChecks.Principal, ExplainResults.Passed, "active", null, null, $"{principal.DisplayName} is active and may sign in.", facts, [])
            : new ExplainedCheck(ExplainChecks.Principal, ExplainResults.Failed, principal.Status, StatusCodes.Status401Unauthorized, PermissionErrors.NotAuthenticated, $"{principal.DisplayName} is {principal.Status}, so they cannot sign in and every request is refused.", facts, []);
    }

    private async Task<ExplainedCheck> EntitlementCheckAsync(EndpointMetadataCollection metadata, string method, Guid principalId, string operation, string route, CancellationToken cancellationToken)
    {
        var request = EntitlementCheck.RequestFor(metadata, method, principalId, operation, route);
        if (request is null)
        {
            return new ExplainedCheck(ExplainChecks.Entitlement, ExplainResults.NotApplicable, "not_licensed", null, null, "The action belongs to no licensed module and needs no feature.", ExplainedCheck.Empty, []);
        }

        var gate = services.GetService<IEntitlementGate>();
        var decision = gate is null ? EntitlementCheck.Unconfirmed(request) : await gate.DecideAsync(request, cancellationToken).ConfigureAwait(false);
        var named = Facts(("licence", request.Licence), ("features", string.Join(',', request.Features)), ("write", request.IsWrite ? "true" : "false"));
        return decision.Allowed
            ? new ExplainedCheck(ExplainChecks.Entitlement, ExplainResults.Passed, "entitled", null, null, "The tenant's subscription includes what the action needs.", named, [])
            : new ExplainedCheck(ExplainChecks.Entitlement, ExplainResults.Failed, decision.Reason ?? "unknown", StatusCodes.Status403Forbidden, decision.ErrorCode, decision.Message ?? "Not part of the subscription.", Facts(("licence", request.Licence), ("features", string.Join(',', request.Features)), ("subject", decision.Subject)), []);
    }

    private async Task<ExplainedCheck> FlagsCheckAsync(EndpointMetadataCollection metadata, Guid principalId, string operation, string route, CancellationToken cancellationToken)
    {
        var request = FlagCheck.RequestFor(metadata, principalId, operation, route);
        if (request is null)
        {
            return new ExplainedCheck(ExplainChecks.Flags, ExplainResults.NotApplicable, "no_flags", null, null, "The action needs no flag and no kill switch.", ExplainedCheck.Empty, []);
        }

        var gate = services.GetService<IFlagGate>();
        var decision = gate is null ? FlagCheck.Unconfirmed(request) : await gate.DecideAsync(request, cancellationToken).ConfigureAwait(false);
        var flags = string.Join(',', request.Flags.Select(f => f.Key));
        return decision.Allowed
            ? new ExplainedCheck(ExplainChecks.Flags, ExplainResults.Passed, "flags_allow", null, null, "Every flag the action needs allows it.", Facts(("flags", flags)), [])
            : new ExplainedCheck(ExplainChecks.Flags, ExplainResults.Failed, decision.ErrorCode == FlagErrors.Killed ? "killed" : "flag_off", FlagCheck.StatusOf(decision), decision.ErrorCode, decision.Message ?? "Not available.", Facts(("flags", flags), ("flag", decision.Flag), ("reason", decision.Reason)), []);
    }

    private async Task<ExplainedCheck> PermissionCheckAsync(EndpointMetadataCollection metadata, Guid principalId, string operation, string route, Func<string, string?> value, CancellationToken cancellationToken)
    {
        if (metadata.GetMetadata<PermissionMetadata>()?.Permission is not { } permission)
        {
            return new ExplainedCheck(ExplainChecks.Permission, ExplainResults.Passed, "signed_in", null, null, "Anyone signed in may call this action; it needs no permission.", ExplainedCheck.Empty, []);
        }

        var scope = metadata.GetMetadata<ScopeMetadata>() is { } scoped
            ? await scoped.ResolveAsync(services, value, cancellationToken).ConfigureAwait(false)
            : AccessScope.Tenant;
        var explanation = await evaluator.ExplainAsync(new AccessRequest(principalId, permission, operation, route) { Scope = scope }, cancellationToken).ConfigureAwait(false);
        var units = await UnitsAsync([scope.UnitId, .. explanation.GrantedBy.Where(g => g.ScopeKind is Scopes.Unit or Scopes.UnitWithChildren).Select(g => g.ScopeId)], cancellationToken).ConfigureAwait(false);
        var actingFor = explanation.ActingForId is { } delegator ? (await principals.FindAsync([delegator], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(delegator) : null;
        var facts = Facts(
            ("permission", permission.Name),
            ("permission_name", permission.Description),
            ("scope", ScopeName(scope.Kind)),
            ("unit_id", scope.UnitId?.ToString("D")),
            ("unit", scope.UnitId is { } unitId ? units.GetValueOrDefault(unitId) : null),
            ("record_type", scope.RecordType),
            ("record_id", scope.RecordId?.ToString("D")),
            ("acting_for", actingFor?.DisplayName),
            ("acting_permissions", explanation.ActingLimit is { } limit ? limit.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
        if (explanation.Allowed)
        {
            var grants = explanation.GrantedBy.Select(g => new ExplainedGrant(
                g.AssignmentId,
                g.RoleId,
                g.RoleCode,
                g.RoleName,
                g.ScopeKind,
                g.ScopeId is { } id && g.ScopeKind is Scopes.Unit or Scopes.UnitWithChildren ? new ExplainedUnit(id, units.GetValueOrDefault(id, string.Empty)) : null,
                g.RecordType,
                g.ScopeKind == Scopes.Record ? g.ScopeId : null)).ToList();
            return new ExplainedCheck(ExplainChecks.Permission, ExplainResults.Passed, AccessDecision.Granted, null, null, $"The permission {permission.Name} is granted at the scope.", facts, grants);
        }

        var reason = explanation.Reason == AccessDecision.NotGranted && explanation.ActingForId is not null && explanation.ActingLimit is { Count: 0 }
            ? "no_active_delegation"
            : explanation.Reason;
        return new ExplainedCheck(ExplainChecks.Permission, ExplainResults.Failed, reason, StatusCodes.Status403Forbidden, PermissionErrors.PermissionDenied, $"The permission {permission.Name} is not granted at the scope.", facts, []);
    }

    private async Task<ExplainedCheck> RecordPolicyCheckAsync(EndpointMetadataCollection metadata, Guid principalId, Func<string, string?> value, CancellationToken cancellationToken)
    {
        if (metadata.GetMetadata<ScopeMetadata>() is not RecordScopeMetadata { PolicyAction: { } action } record
            || !Guid.TryParse(value(record.RouteValue), out var recordId)
            || services.GetService<IRecordPolicies>() is not { } policies)
        {
            return new ExplainedCheck(ExplainChecks.RecordPolicy, ExplainResults.NotApplicable, "no_record", null, null, "The action opens no record through a record policy.", ExplainedCheck.Empty, []);
        }

        var explained = await policies.ExplainAsync(record.RecordType, recordId, action, principalId, cancellationToken).ConfigureAwait(false);
        var facts = Facts(("record_type", record.RecordType), ("record_id", recordId.ToString("D")), ("action", action), ("visible_by", explained.VisibleBy), ("exists", explained.Exists ? "true" : "false"));
        return explained.Answer switch
        {
            RecordPolicyAnswers.Allowed => new ExplainedCheck(ExplainChecks.RecordPolicy, ExplainResults.Passed, "allowed", null, null, $"The record's policy allows {action}.", facts, []),
            RecordPolicyAnswers.Forbidden => new ExplainedCheck(ExplainChecks.RecordPolicy, ExplainResults.Failed, "forbidden", StatusCodes.Status403Forbidden, RecordErrors.Forbidden, $"They may see the record, and its policy refuses {action}.", facts, []),
            RecordPolicyAnswers.NotFound => new ExplainedCheck(ExplainChecks.RecordPolicy, ExplainResults.Failed, explained.Exists ? "not_visible" : "no_such_record", StatusCodes.Status404NotFound, RecordErrors.NotFound, explained.Exists ? "The record exists, and they may not see it." : "There is no such record.", facts, []),
            _ => new ExplainedCheck(ExplainChecks.RecordPolicy, ExplainResults.NotApplicable, "no_policy", null, null, "The record type has no policy.", facts, []),
        };
    }

    private async Task<Dictionary<Guid, string>> UnitsAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToArray();
        return wanted.Length == 0
            ? []
            : await context.OrganisationUnits.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken).ConfigureAwait(false);
    }

    private static ExplainedPrincipal Person(PrincipalSummary principal, IReadOnlyDictionary<Guid, string> units) =>
        new(principal.Id, principal.DisplayName, principal.Type, principal.Status, principal.HomeUnitId is { } home ? new ExplainedUnit(home, units.GetValueOrDefault(home, string.Empty)) : null);

    private static string ScopeName(AccessScopeKind kind) => kind switch
    {
        AccessScopeKind.Unit => "unit",
        AccessScopeKind.Record => "record",
        AccessScopeKind.Anywhere => "anywhere",
        _ => "tenant",
    };

    private static Dictionary<string, string> Facts(params (string Key, string? Value)[] pairs) =>
        pairs.Where(p => !string.IsNullOrEmpty(p.Value)).ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);
}
