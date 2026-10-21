namespace Iron.Kernel.Authorization.Contracts;

/// <summary>
/// Role assignments as other modules use them: Identity gives the first administrator the tenant-administrator role
/// (T10), and reads what a principal may do for <c>/me</c> and for a scope (TF-033).
/// </summary>
public interface IRoleAssignments
{
    /// <summary>Assigns a role to a principal for the whole tenant. Safe to call again; fails with <c>authz.role.not_found</c> for an unknown role.</summary>
    Task<Result> AssignAsync(Guid principalId, string roleCode, CancellationToken cancellationToken);

    /// <summary>
    /// Assigns a role for a scope: the tenant, a unit with or without its children, or a record. A unit that does not
    /// exist or is not active is refused (TF-033 AC4); the assignment and its audit event are one transaction.
    /// </summary>
    Task<Result> AssignAsync(Guid principalId, string roleCode, RoleScope scope, CancellationToken cancellationToken);

    /// <summary>The codes of the roles a principal holds for the whole tenant, in code order.</summary>
    Task<IReadOnlyList<string>> RoleCodesOfAsync(Guid principalId, CancellationToken cancellationToken);

    /// <summary>The codes of the roles a principal holds at any scope, the tenant, a unit or a record, in code order (the MFA policy, ADR-0047).</summary>
    Task<IReadOnlyList<string>> RoleCodesAnywhereAsync(Guid principalId, CancellationToken cancellationToken);

    /// <summary>Every permission the principal holds through its tenant-wide roles, in name order. The shell builds its navigation from it.</summary>
    Task<IReadOnlyList<string>> PermissionsOfAsync(Guid principalId, CancellationToken cancellationToken);

    /// <summary>
    /// Every permission the principal holds at a scope, in name order: the tenant-wide ones, those assigned to the unit
    /// (or to an ancestor with its children), and those assigned to the record.
    /// </summary>
    Task<IReadOnlyList<string>> PermissionsOfAsync(Guid principalId, AccessScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Every permission the principal holds over the whole of a scope, in name order (ADR-0031): for a unit with its
    /// children, only the grants that reach every unit below it. A grant or a removal there is checked against this.
    /// </summary>
    Task<IReadOnlyList<string>> PermissionsOverAsync(Guid principalId, RoleScope scope, CancellationToken cancellationToken);
}
