using Iron.Api.Permissions;
using Iron.Audit.Contracts;
using Iron.Authorization.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Iron.Authorization.Services;

/// <summary>
/// Reads a principal's permissions from its tenant-wide role assignments, caches them for at most 60 seconds, and
/// denies on any failure (TF-031). Every denial is logged with principal, operation and tenant (the tenant is on every
/// log line already) and written as an audit event.
/// </summary>
public sealed class PermissionEvaluator(
    AuthorizationDbContext context,
    IMemoryCache cache,
    PermissionCacheOptions options,
    IAuditWriter audit,
    TimeProvider time,
    ILogger<PermissionEvaluator> logger) : IPermissionEvaluator
{
    public const string DenialAction = "authz.permission_denied";

    /// <summary>Entries are kept in the memory cache for a while; the window itself is checked against Foundation's clock.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public static string CacheKey(Guid principalId) => $"authz:permissions:{principalId:N}";

    public async ValueTask<AccessDecision> EvaluateAsync(AccessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        HashSet<string> granted;
        try
        {
            granted = await GrantedAsync(request.PrincipalId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Permission check unavailable for principal {PrincipalId} on {Operation}; access denied (fail closed).", request.PrincipalId, request.Operation);
            return await DenyAsync(request, AccessDecision.Unavailable, cancellationToken).ConfigureAwait(false);
        }

        if (granted.Contains(request.Permission.Name))
        {
            return AccessDecision.Allow;
        }

        logger.LogWarning("Permission {Permission} denied for principal {PrincipalId} on {Operation}.", request.Permission.Name, request.PrincipalId, request.Operation);
        return await DenyAsync(request, AccessDecision.NotGranted, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> GrantedAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var key = CacheKey(principalId);
        var now = time.GetUtcNow();
        if (cache.TryGetValue(key, out CachedPermissions? cached) && cached is not null && cached.ExpiresAt > now)
        {
            return cached.Granted;
        }

        var permissionLists = await context.RoleAssignments.AsNoTracking()
            .Where(assignment => assignment.PrincipalId == principalId && assignment.ScopeKind == Scopes.Tenant)
            .Join(context.Roles.AsNoTracking(), assignment => assignment.RoleId, role => role.Id, (assignment, role) => role.Permissions)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var granted = new HashSet<string>(permissionLists.SelectMany(list => list), StringComparer.Ordinal);
        var seconds = Math.Clamp(options.CacheSeconds, 0, PermissionCacheOptions.MaxCacheSeconds);
        if (seconds > 0)
        {
            cache.Set(key, new CachedPermissions(granted, now.AddSeconds(seconds)), Retention);
        }

        return granted;
    }

    private sealed record CachedPermissions(HashSet<string> Granted, DateTimeOffset ExpiresAt);

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
