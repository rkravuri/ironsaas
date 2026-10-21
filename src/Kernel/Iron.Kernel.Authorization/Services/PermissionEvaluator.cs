using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// Answers a permission check at its scope (TF-031, TF-033, ADR-0030) from the principal's cached assignments, and
/// denies on any failure. Every denial is logged with principal, operation, scope and tenant (the tenant is on every
/// log line already) and written as an audit event.
/// </summary>
public sealed class PermissionEvaluator(
    AccessGrants grants,
    IAuditWriter audit,
    ILogger<PermissionEvaluator> logger) : IPermissionEvaluator
{
    public const string DenialAction = AuthorizationAuditEvents.PermissionDenied;

    public static string CacheKey(Guid principalId) => AccessGrants.CacheKey(principalId);

    public async ValueTask<AccessDecision> EvaluateAsync(AccessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (explanation, failure) = await DecideAsync(request, cancellationToken).ConfigureAwait(false);
        if (explanation.Allowed)
        {
            return AccessDecision.Allow;
        }

        if (failure is not null)
        {
            // TF-031 AC4's alert: this error line, as the other alerts write theirs, and authz.denials with reason unavailable.
            logger.LogError(failure, "ALERT: the permission check is unavailable for principal {PrincipalId} on {Operation} at {Scope}; access denied (fail closed). Check the unit's database.", request.PrincipalId, request.Operation, request.Scope);
        }
        else
        {
            logger.LogWarning("Permission {Permission} denied for principal {PrincipalId} on {Operation} at {Scope}.", request.Permission.Name, request.PrincipalId, request.Operation, request.Scope);
        }

        return await DenyAsync(request, explanation.Reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The check with what decided it, and without a denial's log line, count and audit event (ADR-0039).</summary>
    public async ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await DecideAsync(request, cancellationToken).ConfigureAwait(false)).Explanation;
    }

    /// <summary>The one decision of a check, for the filters and for an explanation: the grants at the scope, and those that give the permission.</summary>
    private async Task<(AccessExplanation Explanation, Exception? Failure)> DecideAsync(AccessRequest request, CancellationToken cancellationToken)
    {
        ScopedGrants at;
        try
        {
            at = await grants.AtAsync(request.PrincipalId, request.Scope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (new AccessExplanation(false, AccessDecision.Unavailable, []), exception);
        }

        var allowed = at.Permissions.Contains(request.Permission.Name);
        var granting = allowed
            ? at.Giving(request.Permission.Name).Select(g => new GrantingAssignment(g.AssignmentId ?? Guid.Empty, g.RoleId ?? Guid.Empty, g.RoleCode ?? string.Empty, g.RoleName ?? string.Empty, Scopes.NameOf(g.Kind), g.ScopeId, g.RecordType)).ToList()
            : [];
        return (new AccessExplanation(allowed, allowed ? AccessDecision.Granted : AccessDecision.NotGranted, granting, at.Acting?.DelegatorId, at.Acting?.Limit), null);
    }

    private async Task<AccessDecision> DenyAsync(AccessRequest request, string reason, CancellationToken cancellationToken)
    {
        AuthorizationMetrics.Denials.Add(1, new KeyValuePair<string, object?>("reason", reason));
        try
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["permission"] = request.Permission.Name,
                ["reason"] = reason,
                ["path"] = request.Path,
                ["scope"] = request.Scope.ToString(),
            };
            await audit.WriteAsync(new AuditEvent(DenialAction, AuditOutcome.Denied, request.PrincipalId, request.Operation, details), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The denial audit event for principal {PrincipalId} on {Operation} could not be written.", request.PrincipalId, request.Operation);
        }

        return AccessDecision.Deny(reason);
    }
}
