namespace Iron.Kernel.Authorization.Contracts;

/// <summary>The permissions of the Authorization module (TF-030).</summary>
public static class AuthorizationPermissions
{
    public static readonly Permission RolesRead = new("authz.roles.read", "List and view roles and their permissions", Risk.Low);

    public static readonly Permission RolesManage = new("authz.roles.manage", "Create, change and delete tenant roles", Risk.High);

    public static readonly Permission AssignmentsRead = new("authz.assignments.read", "See who holds which role, and where", Risk.Low);

    public static readonly Permission AssignmentsManage = new("authz.assignments.manage", "Assign roles to principals and remove assignments", Risk.High);

    public static readonly Permission UnitsRead = new("authz.units.read", "View the organisation units of this tenant", Risk.Low);

    public static readonly Permission UnitsManage = new("authz.units.manage", "Create, rename, move, deactivate, reactivate and merge organisation units", Risk.High);

    public static readonly Permission DelegationsManage = new("authz.delegations.manage", "Create and end delegations of duties for other people, and see them", Risk.High);

    public static readonly Permission ReviewsManage = new("authz.reviews.manage", "Start and close access review campaigns", Risk.Medium);

    public static readonly Permission ReviewsDecide = new("authz.reviews.decide", "Confirm or revoke the role assignments of a unit in an access review", Risk.High);

    public static IReadOnlyList<Permission> All => [RolesRead, RolesManage, AssignmentsRead, AssignmentsManage, UnitsRead, UnitsManage, DelegationsManage, ReviewsManage, ReviewsDecide];
}

/// <summary>
/// The features of the Authorization module (TF-046, ADR-0034). The kernel is always on; a feature of it is licensed
/// on its own, and role export and import is the one an enterprise plan adds.
/// </summary>
public static class AuthorizationFeatures
{
    public static readonly Feature RoleTransfer = new("authorization.role_transfer", "Export the tenant's roles to a file and import roles from another tenant");

    public static IReadOnlyList<Feature> All => [RoleTransfer];
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
    public const string RoleSystem = "authz.role.system";
    public const string RoleInUse = "authz.role.in_use";
    public const string RoleCodeTaken = "authz.role.code_taken";
    public const string RoleEscalation = "authz.role.escalation";
    public const string RolePermissionUnknown = "authz.role.permission_unknown";
    public const string RolePrincipalType = "authz.role.principal_type";
    public const string ImportFormat = "authz.import.format";
    public const string ImportUnknownPermissions = "authz.import.unknown_permissions";
    public const string AssignmentNotFound = "authz.assignment.not_found";
    public const string AssignmentEscalation = "authz.assignment.escalation";
    public const string AssignmentLastAdministrator = "authz.assignment.last_administrator";
    public const string AssignmentPrincipalType = "authz.assignment.principal_type";
    public const string PrincipalNotFound = "authz.principal.not_found";
    public const string PrincipalRequired = "authz.principal.required";
    public const string ScopeInvalid = "authz.scope.invalid";
    public const string UnitNotFound = "authz.unit.not_found";
    public const string UnitInactive = "authz.unit.inactive";
    public const string UnitCodeTaken = "authz.unit.code_taken";
    public const string UnitKindUnknown = "authz.unit.kind_unknown";
    public const string UnitKindNotAllowed = "authz.unit.kind_not_allowed";
    public const string UnitCycle = "authz.unit.cycle";
    public const string UnitIsRoot = "authz.unit.is_root";
    public const string UnitTooDeep = "authz.unit.too_deep";
    public const string UnitHasActiveChildren = "authz.unit.has_active_children";

    /// <summary>A unit merged into another stays as it is: what it held is in the other unit now (ADR-0038).</summary>
    public const string UnitMerged = "authz.unit.merged";

    /// <summary>A unit cannot merge into itself or a unit below it: the surviving unit would be inside the one that goes.</summary>
    public const string UnitMergeIntoOwn = "authz.unit.merge_into_own";

    /// <summary>A unit cannot be reactivated under a deactivated parent.</summary>
    public const string UnitParentInactive = "authz.unit.parent_inactive";
    public const string DelegationSelf = "authz.delegation.self";
    public const string DelegationServiceAccount = "authz.delegation.service_account";
    public const string DelegationOverlap = "authz.delegation.overlap";
    public const string DelegationCycle = "authz.delegation.cycle";
    public const string DelegationOnward = "authz.delegation.onward";
    public const string DelegationPeriod = "authz.delegation.period";
    public const string DelegationDutyUnknown = "authz.delegation.duty_unknown";
    public const string DelegationNotFound = "authz.delegation.not_found";
    public const string DelegationNotRunning = "authz.delegation.not_running";
    public const string ReviewNotFound = "authz.review.not_found";
    public const string ReviewClosed = "authz.review.closed";
    public const string ReviewUnitsRequired = "authz.review.units_required";
    public const string ReviewItemNotFound = "authz.review.item_not_found";
    public const string ReviewItemDecided = "authz.review.item_decided";
    public const string ReviewDuePassed = "authz.review.due_passed";
    public const string ReviewOwnItem = "authz.review.own_item";
}

/// <summary>Audit event types raised by Authorization (TF-060).</summary>
public static class AuthorizationAuditEvents
{
    public const string PermissionDenied = "authz.permission_denied";
    public const string RoleAssigned = "authz.role_assigned";
    public const string RoleUnassigned = "authz.role_unassigned";
    public const string RoleCreated = "authz.role_created";
    public const string RoleChanged = "authz.role_changed";
    public const string RoleDeleted = "authz.role_deleted";
    public const string UnitCreated = "authz.unit_created";
    public const string UnitRenamed = "authz.unit_renamed";
    public const string UnitMoved = "authz.unit_moved";
    public const string UnitDeactivated = "authz.unit_deactivated";
    public const string UnitReactivated = "authz.unit_reactivated";
    public const string UnitMerged = "authz.unit_merged";
    public const string DelegationCreated = "authz.delegation_created";
    public const string DelegationEnded = "authz.delegation_ended";
    public const string ReviewStarted = "authz.review_started";
    public const string ReviewDecided = "authz.review_decided";
    public const string ReviewClosed = "authz.review_closed";
    public const string ReviewEscalated = "authz.review_escalated";
}
