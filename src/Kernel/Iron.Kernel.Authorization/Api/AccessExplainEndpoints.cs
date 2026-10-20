using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Problems;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Authorization.Api;

/// <summary>The actions whose access can be explained.</summary>
public sealed record ExplainableActionsResponse(IReadOnlyList<ExplainableAction> Data);

/// <summary>
/// Explaining access (TF-039, ADR-0039): why a person can or cannot perform an action, at a unit or on a record. It
/// needs <c>authz.access.explain</c>, which counts at the explained person's home unit (tenant-wide for one without),
/// since it reveals their access; a person who is not of this tenant is not found (AC3). Every explanation is audited.
/// </summary>
internal static class AccessExplainEndpoints
{
    private static readonly string[] Reserved = ["principal", "action", "acting_for"];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var access = endpoints.MapApiGroup("access");
        access.MapGet("/actions", Actions).RequirePermission(AuthorizationPermissions.AccessExplain).ScopedByRow().WithName("ListExplainableActions").WithSummary("The actions whose access can be explained, by module, with the route values each needs.");
        access.MapGet("/explain", ExplainAsync).RequirePermission(AuthorizationPermissions.AccessExplain).ScopedByRow().WithName("ExplainAccess").WithSummary("Why a person can or cannot perform an action, at a unit or on a record: the checks in the order a request meets them.");
    }

    private static IResult Actions(HttpContext http) => TypedResults.Ok(new ExplainableActionsResponse(http.RequestServices.GetRequiredService<AccessExplainer>().Actions()));

    private static async Task<IResult> ExplainAsync(HttpContext http, IPermissionEvaluator evaluator, ICurrentPrincipal reader, IAuditWriter audit, CancellationToken cancellationToken)
    {
        // Resolved here, not as parameters: a host without Identity has no directory, and the parameter would be read as the body.
        var principals = http.RequestServices.GetRequiredService<IPrincipals>();
        var explainer = http.RequestServices.GetRequiredService<AccessExplainer>();
        var query = http.Request.Query;
        if (!Guid.TryParse(query["principal"], out var principalId))
        {
            return ApiResults.Problem(Error.Validation(ApiErrors.Validation, "Say whose access to explain.", new FieldError("principal", "required", "Choose a person.")), http);
        }

        var action = query["action"].ToString();
        if (string.IsNullOrWhiteSpace(action))
        {
            return ApiResults.Problem(Error.Validation(ApiErrors.Validation, "Say which action to explain.", new FieldError("action", "required", "Choose an action.")), http);
        }

        Guid? actingFor = null;
        if (!string.IsNullOrEmpty(query["acting_for"]))
        {
            if (!Guid.TryParse(query["acting_for"], out var delegator))
            {
                return ApiResults.Problem(Error.Validation(ApiErrors.Validation, "Name the person they act for by id.", new FieldError("acting_for", "invalid", "Choose a person.")), http);
            }

            actingFor = delegator;
        }

        // The person must be of this tenant before anything is said of them (TF-039 AC3).
        var found = await principals.FindAsync([principalId], cancellationToken);
        if (!found.TryGetValue(principalId, out var principal))
        {
            return ApiResults.Problem(Error.NotFound(AuthorizationErrors.PrincipalNotFound, "There is no user or service account with this id in this tenant."), http);
        }

        // An explanation reveals the person's access: the permission counts where they belong, their home unit.
        var atHome = new AccessRequest(reader.Id, AuthorizationPermissions.AccessExplain, PermissionFilter.Operation(http), http.Request.Path.Value ?? "/")
        {
            Scope = principal.HomeUnitId is { } home ? AccessScope.Unit(home) : AccessScope.Tenant,
        };
        if (!(await evaluator.EvaluateAsync(atHome, cancellationToken)).Allowed)
        {
            var problem = ProblemFactory.Create(Error.Forbidden(PermissionErrors.PermissionDenied, $"You may not explain the access of people of {principal.DisplayName}'s unit."), http);
            problem.Extensions["permission"] = AuthorizationPermissions.AccessExplain.Name;
            return TypedResults.Problem(problem);
        }

        var values = query.Where(q => !Reserved.Contains(q.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var result = await explainer.ExplainAsync(principalId, action, values, actingFor, cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error, http);
        }

        var explained = result.Value;
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["principal_id"] = principalId.ToString("D"),
            ["action"] = action,
            ["outcome"] = explained.Allowed ? "allowed" : "denied",
            ["status"] = explained.Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (explained.Checks.FirstOrDefault(c => c.Result == ExplainResults.Failed) is { } first)
        {
            details["failed_check"] = first.Check;
            details["reason"] = first.Reason;
        }

        if (actingFor is { } delegatorId)
        {
            details["acting_for"] = delegatorId.ToString("D");
        }

        foreach (var (name, value) in values)
        {
            details[$"route.{name}"] = value;
        }

        await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.AccessExplained, AuditOutcome.Success, reader.Id, principal.DisplayName, details, "principal", principalId), cancellationToken);
        return TypedResults.Ok(explained);
    }
}
