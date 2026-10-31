using Iron.Kernel.History;

namespace Iron.Kernel.Tenancy.Contracts;

/// <summary>Where a tenant stands (TF-001).</summary>
public enum TenantStatus
{
    Provisioning,
    Active,
    Suspended,
    ReadOnly,
    Decommissioned,
}

/// <summary>How big a unit is: the limits of its containers (TF-210, size classes in T14).</summary>
public enum SizeClass
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// A tenant as other modules see it. The fleet provisions from this (T14). <see cref="Plan"/> is the Foundation plan's
/// code; <see cref="Suites"/> are the suites the tenant has (TF-244), which the fleet installs in its unit.
/// </summary>
public sealed record TenantRecord(
    Guid Id,
    string Slug,
    string DisplayName,
    IReadOnlyList<string> Hostnames,
    string Region,
    string Plan,
    SizeClass SizeClass,
    TenantStatus Status,
    string AdministratorLoginId,
    string? AdministratorDisplayName,
    string DatabaseName,
    string StorageContainer,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    bool SampleRecords = false,
    IReadOnlyList<string>? Suites = null)
{
    /// <summary>The suites the tenant has, by short name in order; none for a record made before suites per tenant.</summary>
    public IReadOnlyList<string> InstalledSuites => Suites ?? [];
}

/// <summary>The registry (TF-001) as other modules use it: the fleet reads a tenant and moves its status.</summary>
public interface ITenantRegistry
{
    Task<TenantRecord?> FindAsync(string slug, CancellationToken cancellationToken);

    Task<Result> SetStatusAsync(string slug, TenantStatus status, CancellationToken cancellationToken);
}

public static class TenancyPermissions
{
    public static readonly Permission TenantsRead = new("tenancy.tenants.read", "List and view tenants", Risk.Low);

    public static readonly Permission TenantsManage = new("tenancy.tenants.manage", "Create and change tenants", Risk.High);

    public static readonly Permission TenantsProvision = new("tenancy.tenants.provision", "Request provisioning and de-provisioning of tenants", Risk.Critical);

    public static readonly Permission EntitlementsRead = new("tenancy.entitlements.read", "View plans, tenants' entitlements and their history", Risk.Low);

    public static readonly Permission EntitlementsManage = new("tenancy.entitlements.manage", "Define plans and apply them to tenants", Risk.High);

    /// <summary>
    /// Adding a suite to a tenant or removing it (TF-244 AC3, AC4): the fleet installs it or switches it off in the
    /// tenant's unit by an update job, so it is critical, as provisioning is, with a reason both planes keep.
    /// </summary>
    public static readonly Permission SuitesManage = new("tenancy.suites.manage", "Add a suite to a tenant or remove it, with a reason; the fleet installs it or switches it off", Risk.Critical);

    public static readonly Permission FlagsRead = new("tenancy.flags.read", "View feature flags, their rules, their history and the flag report", Risk.Low);

    public static readonly Permission FlagsManage = new("tenancy.flags.manage", "Set the rules of feature flags", Risk.High);

    /// <summary>Flipping a kill switch stops a critical path for tenants (TF-051 AC4): the control plane's administrators only.</summary>
    public static readonly Permission KillSwitchesManage = new("tenancy.kill_switches.manage", "Turn kill switches on and off, with a reason", Risk.Critical);

    /// <summary>
    /// A recovery link for a tenant's administrator (TF-017, ADR-0046): whoever holds it signs in as that administrator, so
    /// it is the control plane's administrators' only, with a reason the tenant's audit trail shows.
    /// </summary>
    public static readonly Permission AdministratorsRecover = new("tenancy.administrators.recover", "Issue a recovery link for a tenant's administrator, with a reason", Risk.Critical);

    /// <summary>
    /// Signing in as a person of a tenant that allows it, for support (TF-025, ADR-0049): critical, with a reason and a
    /// fresh token, through a one-time link the fleet hands over.
    /// </summary>
    public static readonly Permission ImpersonationStart = new("tenancy.impersonation.start", "Sign in as a person of a tenant that allows it, with a reason, through a one-time link", Risk.Critical);

    /// <summary>Searching a tenant's domain events and replaying them to a subscriber, with a reason, through the fleet (TF-112, ADR-0060).</summary>
    public static readonly Permission EventsManage = new("tenancy.events.manage", "Search a tenant's domain events and replay them to a subscriber, with a reason", Risk.High);

    /// <summary>Asking for emergency access to a tenant, and ending it (TF-026): it needs a second operator to approve.</summary>
    public static readonly Permission BreakGlassRequest = new("tenancy.break_glass.request", "Ask for emergency access to a tenant with a reason and a ticket, and end it", Risk.High);

    /// <summary>Approving another operator's emergency access (TF-026): critical, with a fresh token, never one's own.</summary>
    public static readonly Permission BreakGlassApprove = new("tenancy.break_glass.approve", "Approve another operator's emergency access to a tenant", Risk.Critical);

    /// <summary>The platform's terms of service and privacy notice, which every person accepts (TF-028).</summary>
    public static readonly Permission DocumentsManage = new("tenancy.documents.manage", "Publish new versions of the platform's terms of service and privacy notice", Risk.High);

    /// <summary>
    /// A unit's own client sends its audit anchors here and reads its chain's state (TF-061, ADR-0050): the one
    /// permission of the unit-client role. Every anchor is the sending unit's tenant's, whatever the request says.
    /// </summary>
    public static readonly Permission AuditAnchorsAppend = new("tenancy.audit_anchors.append", "Send a unit's audit anchors and read its chain's verification", Risk.Medium) { ForUnitClients = true };

    /// <summary>Asking the fleet to verify a tenant's audit chain now, beside its daily run (TF-061).</summary>
    public static readonly Permission AuditChainsVerify = new("tenancy.audit_chains.verify", "Ask the fleet to verify a tenant's audit chain now", Risk.High);

    /// <summary>Accepting a tenant's restore point, so its chain continues as a new segment (ADR-0050): critical, with a ticket, a reason and a fresh token.</summary>
    public static readonly Permission AuditRestoresAccept = new("tenancy.audit_restores.accept", "Accept a tenant's audit chain restore point, so the chain continues from it", Risk.Critical);

    /// <summary>
    /// The key officer's permission (TF-075 AC4, ADR-0054): rotating a tenant's keys or its database password, with a
    /// reason and a fresh token. Without it a rotation is refused.
    /// </summary>
    public static readonly Permission KeysManage = new("tenancy.keys.manage", "Rotate a tenant's keys and database password, with a reason", Risk.Critical);

    /// <summary>
    /// A unit's own client reads its keys' rotations and reports its re-encryption's progress (ADR-0054): the unit-client
    /// role's, for the calling unit's tenant only, whatever the request says.
    /// </summary>
    public static readonly Permission KeysReport = new("tenancy.keys.report", "Read the unit's own key rotations and report the re-encryption", Risk.Medium) { ForUnitClients = true };

    /// <summary>A unit's own client sends its security alerts here, after every change (ADR-0056): the calling unit's tenant's only.</summary>
    public static readonly Permission AlertCopiesAppend = new("tenancy.alert_copies.append", "Send a unit's security alerts to the operator", Risk.Medium) { ForUnitClients = true };

    /// <summary>Reading the incidents, their timelines, notices and reviews (TF-079 AC2).</summary>
    public static readonly Permission IncidentsRead = new("tenancy.incidents.read", "Read the incidents, their timelines, notices and reviews", Risk.Low);

    /// <summary>Running an incident: declaring it, noting its timeline, recording its notices, closing it and recording its review.</summary>
    public static readonly Permission IncidentsManage = new("tenancy.incidents.manage", "Declare and run incidents: their timeline, notices to tenants, close and review", Risk.High);

    public static IReadOnlyList<Permission> All => [TenantsRead, TenantsManage, TenantsProvision, EntitlementsRead, EntitlementsManage, SuitesManage, FlagsRead, FlagsManage, KillSwitchesManage, AdministratorsRecover, ImpersonationStart, EventsManage, BreakGlassRequest, BreakGlassApprove, DocumentsManage, AuditAnchorsAppend, AuditChainsVerify, AuditRestoresAccept, KeysManage, KeysReport, AlertCopiesAppend, IncidentsRead, IncidentsManage];
}

public static class TenancyErrors
{
    public const string AlertCopyNotAUnit = "tenancy.alert_copy.not_a_unit";
    public const string IncidentNotFound = "tenancy.incident.not_found";
    public const string IncidentInvalid = "tenancy.incident.invalid";
    public const string IncidentClosed = "tenancy.incident.closed";
    public const string IncidentOpen = "tenancy.incident.open";
    public const string IncidentTenantNotAffected = "tenancy.incident.tenant_not_affected";

    /// <summary>A tenant whose personal data was affected has not been told, so the incident cannot close yet.</summary>
    public const string IncidentNoticeMissing = "tenancy.incident.notice_missing";

    public const string IncidentReviewRecorded = "tenancy.incident.review_recorded";

    public const string TenantNotFound = "tenancy.tenant.not_found";
    public const string SlugTaken = "tenancy.tenant.slug_taken";
    public const string HostnameTaken = "tenancy.hostname.taken";
    public const string HostnameInvalid = "tenancy.hostname.invalid";
    public const string AlreadyActive = "tenancy.tenant.already_active";
    public const string NotProvisionable = "tenancy.tenant.not_provisionable";
    public const string PlanNotFound = "tenancy.plan.not_found";
    public const string PlanCodeInvalid = "tenancy.plan.code_invalid";
    public const string PlanUnknownEntitlements = "tenancy.plan.unknown_entitlements";
    public const string PlanDatesInvalid = "tenancy.plan.dates_invalid";

    /// <summary>A plan names a module or feature of another group than its own, the Foundation's or another suite's (TF-244 AC7).</summary>
    public const string PlanOtherGroup = "tenancy.plan.other_group";

    /// <summary>A plan's group is neither the Foundation nor a suite this build carries.</summary>
    public const string PlanSuiteUnknown = "tenancy.plan.suite_unknown";

    /// <summary>A plan stays in its group: a plan for another group is a new plan.</summary>
    public const string PlanSuiteFixed = "tenancy.plan.suite_fixed";

    /// <summary>Not a suite this build carries.</summary>
    public const string SuiteUnknown = "tenancy.suite.unknown";

    /// <summary>The tenant has the suite already.</summary>
    public const string SuiteInstalled = "tenancy.suite.installed";

    /// <summary>The tenant does not have the suite: add it before applying its plan, or removing it.</summary>
    public const string SuiteNotInstalled = "tenancy.suite.not_installed";

    /// <summary>No plan of the suite was named, and the suite has none or several: name one.</summary>
    public const string SuitePlanRequired = "tenancy.suite.plan_required";

    /// <summary>Adding or removing a suite needs a reason, which the audit trail keeps.</summary>
    public const string SuiteReasonRequired = "tenancy.suite.reason_required";
    public const string HistoryReadOnly = "tenancy.entitlement_history.read_only";
    public const string FlagUnknown = "tenancy.flag.unknown";
    public const string FlagRuleInvalid = "tenancy.flag.rule_invalid";
    public const string FlagValueType = "tenancy.flag.value_type";
    public const string FlagRulesChanged = "tenancy.flag.rules_changed";
    public const string KillSwitchReasonRequired = "tenancy.kill_switch.reason_required";

    /// <summary>The operator's recovery needs a reason: the tenant's administrators read it in their audit trail.</summary>
    public const string RecoveryReasonRequired = "tenancy.recovery.reason_required";
    public const string KillSwitchFalseRule = "tenancy.kill_switch.false_rule";
    public const string KillSwitchUnknown = "tenancy.kill_switch.unknown";

    /// <summary>The operator's impersonation needs a reason (TF-025 AC1).</summary>
    public const string ImpersonationReasonRequired = "tenancy.impersonation.reason_required";
    public const string EventsReasonRequired = "tenancy.events.reason_required";

    public const string BreakGlassNotFound = "tenancy.break_glass.not_found";

    /// <summary>The operator who asked for break-glass tried to approve it (TF-026 AC4).</summary>
    public const string BreakGlassSameOperator = "tenancy.break_glass.same_operator";

    /// <summary>The request is not waiting for approval: approved, expired after 30 minutes (TF-026 AC2), or ended.</summary>
    public const string BreakGlassNotRequested = "tenancy.break_glass.not_requested";

    public const string BreakGlassNotActive = "tenancy.break_glass.not_active";

    public const string BreakGlassDuration = "tenancy.break_glass.duration";

    /// <summary>Not one of the platform's documents.</summary>
    public const string DocumentKeyUnknown = "tenancy.document.key_unknown";

    /// <summary>The caller is no tenant's unit client: only a unit sends anchors, and only its own (ADR-0050).</summary>
    public const string AuditAnchorNotAUnit = "tenancy.audit_anchor.not_a_unit";

    /// <summary>An anchor for a position the tenant anchored before, with another hash: the chain was rewritten, an alert.</summary>
    public const string AuditAnchorConflict = "tenancy.audit_anchor.conflict";

    public const string AuditAnchorKindUnknown = "tenancy.audit_anchor.kind_unknown";

    /// <summary>The tenant's latest verification names no restore point: there is nothing to accept (ADR-0050).</summary>
    public const string AuditRestoreNone = "tenancy.audit_restore.none";

    /// <summary>No anchor counts at the restore point: a full verification names one that does.</summary>
    public const string AuditRestoreNoAnchor = "tenancy.audit_restore.no_anchor";

    /// <summary>Accepting a restore needs a ticket and a reason.</summary>
    public const string AuditRestoreInvalid = "tenancy.audit_restore.invalid";

    /// <summary>Not one of a tenant's keys: data, index, signing or database (ADR-0054).</summary>
    public const string KeyUnknown = "tenancy.key.unknown";

    /// <summary>A rotation needs a reason, which both audit trails keep (TF-075 AC3).</summary>
    public const string KeyReasonRequired = "tenancy.key.reason_required";

    /// <summary>The key is being rotated already: one rotation of a key at a time.</summary>
    public const string KeyRotationInProgress = "tenancy.key_rotation.in_progress";

    public const string KeyRotationNotFound = "tenancy.key_rotation.not_found";

    /// <summary>The caller is no tenant's unit client, or the rotation is another tenant's.</summary>
    public const string KeyRotationNotAUnit = "tenancy.key_rotation.not_a_unit";
}

public static class TenancyAuditEvents
{
    public const string TenantCreated = "tenancy.tenant.created";
    public const string ProvisioningRequested = "tenancy.provisioning_requested";
    public const string DeprovisioningRequested = "tenancy.deprovisioning_requested";
    public const string UpdateRequested = "tenancy.update_requested";

    /// <summary>The operator marked a tenant's unit to run the sample module, or unmarked it (ADR-0057).</summary>
    public const string SampleRecordsChanged = "tenancy.sample_records_changed";
    public const string StatusChanged = "tenancy.tenant.status_changed";
    public const string PlanSaved = "tenancy.plan_saved";
    public const string PlanApplied = "tenancy.plan_applied";

    /// <summary>An operator added a suite to a tenant with its plan and a reason; the fleet's update job installs it (TF-244 AC3).</summary>
    public const string SuiteAdded = "tenancy.suite_added";

    /// <summary>An operator removed a suite from a tenant with a reason; its modules are switched off and its data kept (TF-244 AC4).</summary>
    public const string SuiteRemoved = "tenancy.suite_removed";
    public const string FlagRulesChanged = "tenancy.flag_rules_changed";
    public const string KillSwitchFlipped = "tenancy.kill_switch_flipped";

    /// <summary>The operator asked for a tenant administrator's recovery link, with a reason (ADR-0046); the unit audits its side.</summary>
    public const string AdministratorRecoveryRequested = "tenancy.administrator_recovery_requested";

    /// <summary>The operator asked to sign in as a person of a tenant, with a reason (TF-025); the unit audits its side.</summary>
    public const string ImpersonationRequested = "tenancy.impersonation_requested";

    /// <summary>The operator searched a tenant's domain events (ADR-0060); the job's output is the answer.</summary>
    public const string EventsSearched = "tenancy.events_searched";

    /// <summary>The operator asked to replay a tenant's events to a subscriber, with a reason (ADR-0060); the unit audits its side.</summary>
    public const string EventsReplayRequested = "tenancy.events_replay_requested";

    public const string BreakGlassRequested = "tenancy.break_glass_requested";

    public const string BreakGlassApproved = "tenancy.break_glass_approved";

    /// <summary>Not approved within 30 minutes (TF-026 AC2).</summary>
    public const string BreakGlassExpired = "tenancy.break_glass_expired";

    /// <summary>At its time limit, or ended early by an operator: the account is being closed.</summary>
    public const string BreakGlassEnding = "tenancy.break_glass_ending";

    /// <summary>Closed, with the review of every action (TF-026 AC3).</summary>
    public const string BreakGlassEnded = "tenancy.break_glass_ended";

    public const string BreakGlassFailed = "tenancy.break_glass_failed";

    /// <summary>The operator published a version of the platform's terms of service or privacy notice (TF-028).</summary>
    public const string PlatformDocumentPublished = "tenancy.platform_document_published";

    /// <summary>An operator asked the fleet to verify a tenant's audit chain now (TF-061).</summary>
    public const string AuditVerificationRequested = "tenancy.audit_verification_requested";

    /// <summary>A verification found a changed or deleted event, or a chain that disagrees with its anchors (TF-061 AC2).</summary>
    public const string AuditChainBroken = "tenancy.audit_chain_broken";

    /// <summary>A verification found the chain ending before its latest anchor: the restore point is named (TF-061 AC3).</summary>
    public const string AuditChainRestored = "tenancy.audit_chain_restored";

    /// <summary>A unit sent an anchor for a position it anchored before, with another hash.</summary>
    public const string AuditAnchorConflict = "tenancy.audit_anchor_conflict";

    /// <summary>An operator accepted a restore point; the chain continues as a new segment (ADR-0050).</summary>
    public const string AuditRestoreAccepted = "tenancy.audit_restore_accepted";

    /// <summary>A key officer, or the schedule, asked for a key or database password to be rotated, with the reason (TF-075 AC3).</summary>
    public const string KeyRotationRequested = "tenancy.key_rotation_requested";

    /// <summary>The fleet rotated a key in the key service, or a database password (ADR-0054).</summary>
    public const string KeyRotated = "tenancy.key_rotated";

    /// <summary>The old versions of a key were retired after every value moved, or every token expired (TF-075 AC1, AC2).</summary>
    public const string KeyRetired = "tenancy.key_retired";

    public const string KeyRotationFailed = "tenancy.key_rotation_failed";

    /// <summary>An operator declared an incident, with its severity, the tenants affected, its lead and a summary (TF-079 AC2).</summary>
    public const string IncidentDeclared = "tenancy.incident_declared";

    /// <summary>An entry of the incident's timeline or its communication log.</summary>
    public const string IncidentNoted = "tenancy.incident_noted";

    /// <summary>A tenant was found affected, or found to have its personal data affected, from when that became known.</summary>
    public const string IncidentTenantAffected = "tenancy.incident_tenant_affected";

    /// <summary>The notice of a personal data breach was sent to an affected tenant (the 72-hour duty).</summary>
    public const string IncidentNoticeSent = "tenancy.incident_notice_sent";

    public const string IncidentClosed = "tenancy.incident_closed";

    /// <summary>The post-incident review: what happened, why, what changes (TF-079 AC2).</summary>
    public const string IncidentReviewRecorded = "tenancy.incident_review_recorded";

    /// <summary>Ten days after the close and still no review: the escalation, at high severity, which an alert rule raises.</summary>
    public const string IncidentReviewOverdue = "tenancy.incident_review_overdue";

    /// <summary>Seventy-two hours after a breach became known and a tenant not yet told.</summary>
    public const string IncidentNoticeOverdue = "tenancy.incident_notice_overdue";

    /// <summary>Every event type above with the words it reads as in the audit trail, the History tab and the exports (ADR-0051).</summary>
    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        new(TenantCreated, "Tenant created"),
        new(ProvisioningRequested, "Provisioning requested"),
        new(DeprovisioningRequested, "Deprovisioning requested"),
        new(UpdateRequested, "Update requested"),
        new(SampleRecordsChanged, "Sample records switched on or off for a tenant"),
        new(StatusChanged, "Tenant status changed"),
        new(PlanSaved, "Plan saved"),
        new(PlanApplied, "Plan applied"),
        new(SuiteAdded, "Suite added to a tenant"),
        new(SuiteRemoved, "Suite removed from a tenant"),
        new(FlagRulesChanged, "Feature flag rules changed"),
        new(KillSwitchFlipped, "Kill switch changed"),
        new(AdministratorRecoveryRequested, "Administrator recovery requested"),
        new(ImpersonationRequested, "Sign-in-as requested"),
        new(EventsSearched, "A tenant's events searched"),
        new AuditEventType(EventsReplayRequested, "A tenant's events replayed").AsAdministrative(),
        new(BreakGlassRequested, "Break-glass access requested"),
        new(BreakGlassApproved, "Break-glass access approved"),
        new(BreakGlassExpired, "Break-glass request expired"),
        new(BreakGlassEnding, "Break-glass access ending"),
        new(BreakGlassEnded, "Break-glass access ended"),
        new(BreakGlassFailed, "Break-glass access failed"),
        new(PlatformDocumentPublished, "Platform document published"),
        new(AuditVerificationRequested, "Audit chain verification requested"),
        new(AuditChainBroken, "Audit chain found broken"),
        new(AuditChainRestored, "Audit chain found restored"),
        new(AuditAnchorConflict, "Conflicting audit anchor"),
        new(AuditRestoreAccepted, "Audit chain restore accepted"),
        new(KeyRotationRequested, "Key rotation requested"),
        new(KeyRotated, "Key rotated"),
        new(KeyRetired, "Old key versions retired"),
        new(KeyRotationFailed, "Key rotation failed"),
        new(IncidentDeclared, "Incident declared"),
        new(IncidentNoted, "Incident noted"),
        new(IncidentTenantAffected, "Tenant affected by an incident"),
        new(IncidentNoticeSent, "Breach notice sent to a tenant"),
        new(IncidentClosed, "Incident closed"),
        new(IncidentReviewRecorded, "Post-incident review recorded"),
        new(IncidentReviewOverdue, "Post-incident review overdue"),
        new(IncidentNoticeOverdue, "Breach notice overdue"),
    ];
}

/// <summary>
/// A tenant's flag rules as the fleet uses them (ADR-0035): written to the store again, with the platform's, which the
/// secrets step does on every provisioning and update, so a store that lost them heals with the unit.
/// </summary>
public interface ITenantFlags
{
    /// <summary>Writes <c>platform/flags</c> and <c>tenants/&lt;slug&gt;/flags</c>; false when the store could not take them (the fleet reconciles every minute).</summary>
    Task<bool> PublishAsync(string slug, CancellationToken cancellationToken);
}

/// <summary>
/// A tenant's entitlements as the fleet uses them (ADR-0034): written to the store at <c>tenants/&lt;slug&gt;/entitlements</c>
/// again, which the secrets step does on every provisioning and update, so a store that lost them heals with the unit.
/// </summary>
public interface ITenantEntitlements
{
    /// <summary>Writes the tenant's effective entitlements to the store; false when the store could not take them (the fleet reconciles every minute).</summary>
    Task<bool> PublishAsync(string slug, CancellationToken cancellationToken);
}
