using Iron.Core.Results;

namespace Iron.Authorization.Contracts;

/// <summary>Role assignments for the whole tenant, as other modules use them: Identity gives the first administrator the tenant-administrator role (T10).</summary>
public interface IRoleAssignments
{
    /// <summary>Assigns a role to a principal for the whole tenant. Safe to call again; fails with <c>authz.role.not_found</c> for an unknown role.</summary>
    Task<Result> AssignAsync(Guid principalId, string roleName, CancellationToken cancellationToken);

    /// <summary>The names of the roles a principal holds for the whole tenant, in name order.</summary>
    Task<IReadOnlyList<string>> RoleNamesOfAsync(Guid principalId, CancellationToken cancellationToken);

    /// <summary>Every permission the principal holds through its tenant-wide roles, in name order. The shell builds its navigation from it.</summary>
    Task<IReadOnlyList<string>> PermissionsOfAsync(Guid principalId, CancellationToken cancellationToken);
}
