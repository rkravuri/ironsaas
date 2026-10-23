using Iron.Kernel.History;

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

    /// <summary>Explain why a person can or cannot perform an action (TF-039): it reveals their access, so it is high risk.</summary>
    public static readonly Permission AccessExplain = new("authz.access.explain", "Explain why a person can or cannot perform an action", Risk.High);

    /// <summary>Read the conflict rules and the report of existing conflicts (TF-036).</summary>
    public static readonly Permission SodRead = new("authz.sod.read", "See the conflict rules and the conflicts report", Risk.Medium);

    /// <summary>Define, change and remove conflict rules, and run the report now (TF-036).</summary>
    public static readonly Permission SodManage = new("authz.sod.manage", "Define the conflict rules between permissions or roles, and run the report", Risk.High);

    public static IReadOnlyList<Permission> All => [RolesRead, RolesManage, AssignmentsRead, AssignmentsManage, UnitsRead, UnitsManage, DelegationsManage, ReviewsManage, ReviewsDecide, AccessExplain, SodRead, SodManage];
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

    /// <summary>
    /// An operator's emergency access (TF-026, ADR-0049): every permission of the tenant but the critical ones, so
    /// break-glass repairs a tenant without changing who signs in through which provider or signing in as someone else.
    /// </summary>
    public const string BreakGlass = "break-glass";

    public static IReadOnlyList<string> All => [TenantAdministrator, ReadOnly, ServiceDefault, BreakGlass];

    /// <summary>
    /// A tenant unit's own client in the control plane (ADR-0050): the permissions marked for unit clients and nothing
    /// else, so a unit can send its audit anchors and read its own chain's state. Seeded only where such permissions
    /// exist, the control plane; it is not one of <see cref="All"/>, which every tenant has.
    /// </summary>
    public const string UnitClient = "unit-client";
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

    /// <summary>No endpoint of that name to explain (ADR-0039).</summary>
    public const string ExplainActionUnknown = "authz.explain.action_unknown";

    /// <summary>A block rule refuses a grant or a delegation that would create its conflict; the message names the rule (TF-036 AC1).</summary>
    public const string SodConflict = "authz.sod.conflict";

    public const string SodRuleNotFound = "authz.sod.rule_not_found";
    public const string SodRuleInvalid = "authz.sod.rule_invalid";
    public const string SodSideUnknown = "authz.sod.side_unknown";
    public const string SodSidesSame = "authz.sod.sides_same";
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

    /// <summary>Someone's access to an action was explained (ADR-0039): it reveals their access, so it is on the record.</summary>
    public const string AccessExplained = "authz.access_explained";

    public const string SodRuleCreated = "authz.sod_rule_created";
    public const string SodRuleChanged = "authz.sod_rule_changed";
    public const string SodRuleDeleted = "authz.sod_rule_deleted";
    public const string SodReportRun = "authz.sod_report_run";

    /// <summary>Every event type above with the words it reads as in the audit trail, the History tab and the exports (ADR-0051).</summary>
    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        new(PermissionDenied, "Permission denied"),
        new AuditEventType(RoleAssigned, "Role assigned").AsAdministrative(),
        new AuditEventType(RoleUnassigned, "Role removed from a person").AsAdministrative(),
        new AuditEventType(RoleCreated, "Role created").AsAdministrative(),
        new AuditEventType(RoleChanged, "Role changed").AsAdministrative(),
        new AuditEventType(RoleDeleted, "Role deleted").AsAdministrative(),
        new AuditEventType(UnitCreated, "Unit created").AsAdministrative(),
        new AuditEventType(UnitRenamed, "Unit renamed").AsAdministrative(),
        new AuditEventType(UnitMoved, "Unit moved").AsAdministrative(),
        new AuditEventType(UnitDeactivated, "Unit deactivated").AsAdministrative(),
        new AuditEventType(UnitReactivated, "Unit reactivated").AsAdministrative(),
        new AuditEventType(UnitMerged, "Units merged").AsAdministrative(),
        new AuditEventType(DelegationCreated, "Delegation created").AsAdministrative(),
        new AuditEventType(DelegationEnded, "Delegation ended").AsAdministrative(),
        new AuditEventType(ReviewStarted, "Access review started").AsAdministrative(),
        new AuditEventType(ReviewDecided, "Access review decision made").AsAdministrative(),
        new AuditEventType(ReviewClosed, "Access review closed").AsAdministrative(),
        new(ReviewEscalated, "Access review escalated"),
        new(AccessExplained, "Access explained"),
        new AuditEventType(SodRuleCreated, "Segregation of duties rule created").AsAdministrative(),
        new AuditEventType(SodRuleChanged, "Segregation of duties rule changed").AsAdministrative(),
        new AuditEventType(SodRuleDeleted, "Segregation of duties rule deleted").AsAdministrative(),
        new AuditEventType(SodReportRun, "Segregation of duties report run").AsAdministrative(),
    ];
}
