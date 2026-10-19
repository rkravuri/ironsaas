namespace Iron.Kernel.Authorization.Contracts;

/// <summary>The permissions of the Authorization module (TF-030).</summary>
public static class AuthorizationPermissions
{
    public static readonly Permission RolesRead = new("authz.roles.read", "List and view roles and their permissions", Risk.Low);

    public static readonly Permission RolesManage = new("authz.roles.manage", "Create, change and delete tenant roles", Risk.High);

    public static readonly Permission AssignmentsManage = new("authz.assignments.manage", "Assign roles to principals and remove assignments", Risk.High);

    public static IReadOnlyList<Permission> All => [RolesRead, RolesManage, AssignmentsManage];
}

/// <summary>The system roles every tenant has (TF-032). They cannot be edited; the seeder keeps them in step with the catalogue.</summary>
public static class SystemRoles
{
    public const string TenantAdministrator = "tenant-administrator";
    public const string ReadOnly = "read-only";
    public const string ServiceDefault = "service-default";

    public static IReadOnlyList<string> All => [TenantAdministrator, ReadOnly, ServiceDefault];
}

public static class AuthorizationErrors
{
    public const string RoleNotFound = "authz.role.not_found";
}
