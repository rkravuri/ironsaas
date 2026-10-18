using Iron.Authorization.Contracts;
using Iron.Authorization.Data;
using Iron.Core.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Iron.Authorization.Services;

public sealed class RoleAssignments(AuthorizationDbContext context, IMemoryCache cache) : IRoleAssignments
{
    public async Task<Result> AssignAsync(Guid principalId, string roleName, CancellationToken cancellationToken)
    {
        if (principalId == Guid.Empty)
        {
            return Error.Validation("authz.principal.required", "A principal is required.");
        }

        var role = await context.Roles.SingleOrDefaultAsync(r => r.Name == roleName, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Error.NotFound(AuthorizationErrors.RoleNotFound, $"There is no role named '{roleName}'.");
        }

        var exists = await context.RoleAssignments
            .AnyAsync(a => a.PrincipalId == principalId && a.RoleId == role.Id && a.ScopeKind == Scopes.Tenant && a.ScopeId == null, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            context.RoleAssignments.Add(new RoleAssignment(role.Id, principalId));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        cache.Remove(PermissionEvaluator.CacheKey(principalId)); // the grant shows on the next request, not after the cache window
        return Result.Success();
    }

    public async Task<IReadOnlyList<string>> RoleNamesOfAsync(Guid principalId, CancellationToken cancellationToken)
    {
        return await context.RoleAssignments.AsNoTracking()
            .Where(a => a.PrincipalId == principalId && a.ScopeKind == Scopes.Tenant)
            .Join(context.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => r.Name)
            .OrderBy(name => name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
