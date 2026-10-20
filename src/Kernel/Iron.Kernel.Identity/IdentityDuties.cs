using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Identity.Contracts;

namespace Iron.Kernel.Identity;

/// <summary>
/// Identity's duties (TF-037, ADR-0037). User administration covers reading and managing people and their access: the
/// users, their invitations, their role assignments, and the roles and units an assignment names. Approval, receiving
/// and evaluation are declared by the slices that build them.
/// </summary>
public static class IdentityDuties
{
    public static readonly Duty UserAdministration = new(
        "identity.user_administration",
        "user administration",
        "Reading and managing people: their accounts, their invitations, and the roles they hold where.",
        [
            IdentityPermissions.UsersRead,
            IdentityPermissions.UsersManage,
            AuthorizationPermissions.AssignmentsRead,
            AuthorizationPermissions.AssignmentsManage,
            AuthorizationPermissions.RolesRead,
            AuthorizationPermissions.UnitsRead,
        ]);

    public static IReadOnlyList<Duty> All => [UserAdministration];
}
