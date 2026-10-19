using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
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

        IReadOnlySet<string> granted;
        try
        {
            granted = await grants.PermissionsAtAsync(request.PrincipalId, request.Scope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Permission check unavailable for principal {PrincipalId} on {Operation} at {Scope}; access denied (fail closed).", request.PrincipalId, request.Operation, request.Scope);
            return await DenyAsync(request, AccessDecision.Unavailable, cancellationToken).ConfigureAwait(false);
        }

        if (granted.Contains(request.Permission.Name))
        {
            return AccessDecision.Allow;
        }

        logger.LogWarning("Permission {Permission} denied for principal {PrincipalId} on {Operation} at {Scope}.", request.Permission.Name, request.PrincipalId, request.Operation, request.Scope);
        return await DenyAsync(request, AccessDecision.NotGranted, cancellationToken).ConfigureAwait(false);
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
