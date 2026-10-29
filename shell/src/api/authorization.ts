import { z } from "zod";
import { listSchema, request } from "./client";

/** One organisation unit as the Authorization API writes it (TF-034): no business fields, a kind code (ADR-0029). */
export const UnitSchema = z.object({
  id: z.string(),
  name: z.string(),
  code: z.string(),
  kind: z.string(),
  parent_id: z.string().nullable(),
  depth: z.number(),
  status: z.enum(["active", "inactive"]),
  created_at: z.string(),
  updated_at: z.string(),
  version: z.number(),
  /** The unit this one was merged into (ADR-0038); a merged unit stays inactive. */
  merged_into_id: z.string().nullable().optional(),
});
export type Unit = z.infer<typeof UnitSchema>;

/** GET /api/v1/organisation-units: the part of the tree the reader may see, depth first, siblings by name (ADR-0031). */
export const UnitTreeSchema = z.object({ data: z.array(UnitSchema) });

export const KindSchema = z.object({
  code: z.string(),
  name: z.string(),
  child_kinds: z.array(z.string()),
});
export type Kind = z.infer<typeof KindSchema>;

/** GET /api/v1/organisation-kinds: what the kinds provider offers and which kinds each may hold. */
export const KindsSchema = z.object({ data: z.array(KindSchema), root_kind: z.string() });
export type Kinds = z.infer<typeof KindsSchema>;

const units = "/api/v1/organisation-units";

export const fetchUnits = (signal?: AbortSignal) =>
  request(units, { schema: UnitTreeSchema, ...(signal ? { signal } : {}) });

export const fetchKinds = (signal?: AbortSignal) =>
  request("/api/v1/organisation-kinds", { schema: KindsSchema, ...(signal ? { signal } : {}) });

export const createUnit = (parentId: string, body: { name: string; code: string; kind: string }) =>
  request(`${units}/${encodeURIComponent(parentId)}/children`, { method: "POST", body, schema: UnitSchema });

export const renameUnit = (id: string, name: string) =>
  request(`${units}/${encodeURIComponent(id)}/rename`, { method: "POST", body: { name }, schema: UnitSchema });

export const moveUnit = (id: string, parentId: string) =>
  request(`${units}/${encodeURIComponent(id)}/move`, {
    method: "POST",
    body: { parent_id: parentId },
    schema: UnitSchema,
  });

export const deactivateUnit = (id: string) =>
  request(`${units}/${encodeURIComponent(id)}/deactivate`, { method: "POST", schema: UnitSchema });

export const reactivateUnit = (id: string) =>
  request(`${units}/${encodeURIComponent(id)}/reactivate`, { method: "POST", schema: UnitSchema });

/** GET .../readiness: what the modules need of a unit before people work in it (TF-223); ready when every item is done. */
export const ReadinessSchema = z.object({
  unit_id: z.string(),
  ready: z.boolean(),
  items: z.array(z.object({ module: z.string(), code: z.string(), label: z.string(), done: z.boolean() })),
});
export type Readiness = z.infer<typeof ReadinessSchema>;

export const fetchReadiness = (id: string, signal?: AbortSignal) =>
  request(`${units}/${encodeURIComponent(id)}/readiness`, { schema: ReadinessSchema, ...(signal ? { signal } : {}) });

/** The kinds of thing a merge moves, in the order the preview lists them (ADR-0038). */
export const mergePartCodes = ["records", "people", "assignments", "settings", "shares", "children"] as const;

/** One kind of thing a merge moves: the module, for records the type's code and display name, at most 20 in words, all of them counted, and what is dropped. */
export const MergePartSchema = z.object({
  code: z.string(),
  module: z.string(),
  record_type: z.string().nullable(),
  record_type_name: z.string().nullable(),
  count: z.number(),
  items: z.array(z.string()),
  conflicts: z.array(z.string()),
});
export type MergePart = z.infer<typeof MergePartSchema>;

/** A merge of one unit into another, previewed (nothing changed) or done (what moved). */
export const MergeSchema = z.object({ from: UnitSchema, into: UnitSchema, parts: z.array(MergePartSchema) });
export type Merge = z.infer<typeof MergeSchema>;

export const previewMerge = (id: string, into: string, signal?: AbortSignal) =>
  request(`${units}/${encodeURIComponent(id)}/merge-preview?into=${encodeURIComponent(into)}`, {
    schema: MergeSchema,
    ...(signal ? { signal } : {}),
  });

export const mergeUnit = (id: string, into: string) =>
  request(`${units}/${encodeURIComponent(id)}/merge`, { method: "POST", body: { into_id: into }, schema: MergeSchema });

/** Who sees a business record beyond its unit (TF-222, ADR-0038). */
export const SharingModeSchema = z.enum(["private", "shared", "tenant"]);
export type SharingMode = z.infer<typeof SharingModeSchema>;

const SharingUnitSchema = z.object({ id: z.string(), name: z.string(), code: z.string() });
export type SharingUnit = z.infer<typeof SharingUnitSchema>;

/** A unit the record may be shared with, placed on the tree. */
export const SharingChoiceSchema = SharingUnitSchema.extend({ parent_id: z.string().nullable(), depth: z.number() });
export type SharingChoice = z.infer<typeof SharingChoiceSchema>;

/**
 * GET /api/v1/record-sharing/{type}/{id}: the record's unit, the mode, the units it is shared with, whether the reader
 * may change it and, if so, the active units to choose from in tree order.
 */
export const RecordSharingSchema = z.object({
  record_type: z.string(),
  record_id: z.string(),
  owning_unit: SharingUnitSchema,
  mode: SharingModeSchema,
  units: z.array(SharingUnitSchema),
  may_change: z.boolean(),
  choices: z.array(SharingChoiceSchema),
});
export type RecordSharing = z.infer<typeof RecordSharingSchema>;

const sharingPath = (recordType: string, id: string) =>
  `/api/v1/record-sharing/${encodeURIComponent(recordType)}/${encodeURIComponent(id)}`;

export const fetchSharing = (recordType: string, id: string, signal?: AbortSignal) =>
  request(sharingPath(recordType, id), { schema: RecordSharingSchema, ...(signal ? { signal } : {}) });

export const changeSharing = (recordType: string, id: string, body: { mode: SharingMode; units: string[] }) =>
  request(sharingPath(recordType, id), { method: "PUT", body, schema: RecordSharingSchema });

/** Who a role is for (TF-032): people, service accounts, or both (only the tenant administrator system role). */
export const PrincipalKindSchema = z.enum(["user", "service", "any"]);
export type PrincipalKind = z.infer<typeof PrincipalKindSchema>;

/** One role as the Authorization API writes it (ADR-0031): the code identifies it, the name is for people. */
export const RoleSchema = z.object({
  id: z.string(),
  code: z.string(),
  name: z.string(),
  description: z.string(),
  is_system: z.boolean(),
  principal_type: PrincipalKindSchema,
  permissions: z.array(z.string()),
  created_at: z.string(),
  updated_at: z.string(),
  version: z.number(),
});
export type Role = z.infer<typeof RoleSchema>;
export const RoleListSchema = listSchema(RoleSchema);

export const RiskSchema = z.enum(["low", "medium", "high", "critical"]);
export type Risk = z.infer<typeof RiskSchema>;

/** One permission of the catalogue (TF-030): the role editor groups them by module and shows the risk. */
export const PermissionSchema = z.object({
  name: z.string(),
  module: z.string(),
  description: z.string(),
  risk: RiskSchema,
});
export type Permission = z.infer<typeof PermissionSchema>;
export const PermissionsSchema = z.object({ data: z.array(PermissionSchema) });

/** The export of a tenant's roles (TF-032): a format version and each role's code, name, description, type and permissions. */
export const RoleDocumentSchema = z.object({
  code: z.string(),
  name: z.string(),
  description: z.string(),
  principal_type: PrincipalKindSchema,
  permissions: z.array(z.string()),
});
export const RoleExportSchema = z.object({ format_version: z.number(), roles: z.array(RoleDocumentSchema) });
export type RoleExport = z.infer<typeof RoleExportSchema>;

/** What an import does or would do (TF-032 AC3). */
export const ImportDiffSchema = z.object({
  dry_run: z.boolean(),
  applied: z.boolean(),
  created: z.array(z.string()),
  changed: z.array(
    z.object({
      code: z.string(),
      added: z.array(z.string()),
      removed: z.array(z.string()),
      fields: z.array(z.string()),
    }),
  ),
  unchanged: z.array(z.string()),
  unknown: z.array(z.object({ code: z.string(), permissions: z.array(z.string()) })),
  skipped_system: z.array(z.string()),
});
export type ImportDiff = z.infer<typeof ImportDiffSchema>;

export interface RoleInput {
  name: string;
  description: string;
  principal_type: "user" | "service";
  permissions: string[];
}

const roles = "/api/v1/roles";

export const fetchRoles = (query: string, signal?: AbortSignal) =>
  request(`${roles}${query ? `?${query}` : ""}`, { schema: RoleListSchema, ...(signal ? { signal } : {}) });

export const fetchRole = (id: string, signal?: AbortSignal) =>
  request(`${roles}/${encodeURIComponent(id)}`, { schema: RoleSchema, ...(signal ? { signal } : {}) });

export const createRole = (code: string, input: RoleInput) =>
  request(roles, { method: "POST", body: { code, ...input }, schema: RoleSchema });

export const updateRole = (id: string, input: RoleInput) =>
  request(`${roles}/${encodeURIComponent(id)}`, { method: "PUT", body: input, schema: RoleSchema });

export const deleteRole = (id: string) => request(`${roles}/${encodeURIComponent(id)}`, { method: "DELETE" });

export const fetchPermissions = (signal?: AbortSignal) =>
  request("/api/v1/permissions", { schema: PermissionsSchema, ...(signal ? { signal } : {}) });

export const exportRoles = () => request(`${roles}/export`, { schema: RoleExportSchema });

export const importRoles = (document: unknown, dryRun: boolean) =>
  request(`${roles}/import`, { method: "POST", body: { dry_run: dryRun, document }, schema: ImportDiffSchema });

export const ScopeKindSchema = z.enum(["tenant", "unit", "unit_with_children", "record"]);
export type ScopeKind = z.infer<typeof ScopeKindSchema>;

/** One role assignment as a reader sees it (TF-033): who, which role, where, and the unit's path from the root. */
export const AssignmentSchema = z.object({
  id: z.string(),
  principal_id: z.string(),
  principal_name: z.string().nullable(),
  role_id: z.string(),
  role_code: z.string(),
  role_name: z.string(),
  scope_kind: ScopeKindSchema,
  unit_id: z.string().nullable(),
  record_type: z.string().nullable(),
  record_id: z.string().nullable(),
  unit_path: z.array(z.object({ id: z.string(), name: z.string() })),
  created_at: z.string(),
});
export type Assignment = z.infer<typeof AssignmentSchema>;
export const AssignmentListSchema = listSchema(AssignmentSchema);

const assignments = "/api/v1/role-assignments";

/** GET /api/v1/role-assignments/mine: the signed-in principal's own assignments, whatever it may read of others' (D4). */
export const MyAssignmentsSchema = z.object({ data: z.array(AssignmentSchema) });

export const fetchMyAssignments = (signal?: AbortSignal) =>
  request(`${assignments}/mine`, { schema: MyAssignmentsSchema, ...(signal ? { signal } : {}) });

export const fetchAssignments = (query: string, signal?: AbortSignal) =>
  request(`${assignments}?${query}`, { schema: AssignmentListSchema, ...(signal ? { signal } : {}) });

export const grantRole = (body: { principal_id: string; role_id: string; scope_kind: ScopeKind; unit_id?: string }) =>
  request(assignments, { method: "POST", body, schema: AssignmentSchema });

export const removeAssignment = (id: string) =>
  request(`${assignments}/${encodeURIComponent(id)}`, { method: "DELETE" });

/** A duty a person may hand to someone for a while (TF-037, ADR-0037): its code, its words, and what it covers. */
export const DutySchema = z.object({
  code: z.string(),
  module: z.string(),
  name: z.string(),
  description: z.string(),
  permissions: z.array(z.string()),
});
export type Duty = z.infer<typeof DutySchema>;
export const DutiesSchema = z.object({ data: z.array(DutySchema) });

export const fetchDuties = (signal?: AbortSignal) =>
  request("/api/v1/duties", { schema: DutiesSchema, ...(signal ? { signal } : {}) });

export const DelegationStatusSchema = z.enum(["scheduled", "active", "ended", "expired"]);
export type DelegationStatus = z.infer<typeof DelegationStatusSchema>;

const PersonSchema = z.object({ id: z.string(), name: z.string() });

/** A delegation as its people and administrators see it: who, to whom, which duties, when, why, and where it stands. */
export const DelegationSchema = z.object({
  id: z.string(),
  delegator: PersonSchema,
  delegate: PersonSchema,
  duties: z.array(z.object({ code: z.string(), name: z.string() })),
  starts_at: z.string(),
  ends_at: z.string(),
  reason: z.string(),
  status: DelegationStatusSchema,
  ended_at: z.string().nullable(),
  end_reason: z.string().nullable(),
  created_by: z.string(),
  created_at: z.string(),
});
export type Delegation = z.infer<typeof DelegationSchema>;
export const MyDelegationsSchema = z.object({ data: z.array(DelegationSchema) });
export const DelegationListSchema = listSchema(DelegationSchema);

const delegations = "/api/v1/delegations";

/** The delegations you give and hold: a handful, not a paged list. */
export const fetchMyDelegations = (signal?: AbortSignal) =>
  request(`${delegations}/mine`, { schema: MyDelegationsSchema, ...(signal ? { signal } : {}) });

/** Others' delegations, for administrators: those whose delegator's home unit you hold authz.delegations.manage at. */
export const fetchManagedDelegations = (query: string, signal?: AbortSignal) =>
  request(`${delegations}${query ? `?${query}` : ""}`, { schema: DelegationListSchema, ...(signal ? { signal } : {}) });

export interface NewDelegation {
  /** Left out for your own; someone else's needs authz.delegations.manage at their home unit. */
  delegator_id?: string;
  delegate_id: string;
  duties: string[];
  starts_at: string;
  ends_at: string;
  reason: string;
}

export const createDelegation = (body: NewDelegation) =>
  request(delegations, { method: "POST", body, schema: DelegationSchema });

export const endDelegation = (id: string, reason?: string) =>
  request(`${delegations}/${encodeURIComponent(id)}/end`, {
    method: "POST",
    body: { reason: reason ?? null },
    schema: DelegationSchema,
  });

/** Access reviews (TF-038). */
export const ReviewStatusSchema = z.enum(["open", "closed"]);
export const ReviewDecisionSchema = z.enum(["confirm", "revoke"]);
export type ReviewDecision = z.infer<typeof ReviewDecisionSchema>;

export const ReviewProgressSchema = z.object({
  total: z.number(),
  decided: z.number(),
  confirmed: z.number(),
  revoked: z.number(),
  open: z.number(),
  overdue: z.number(),
});
export type ReviewProgress = z.infer<typeof ReviewProgressSchema>;

/** What a campaign reviews: role assignments, or who holds which visibility tag (TF-241 AC7, ADR-0057). */
export const ReviewSubjectSchema = z.enum(["roles", "visibility_tags"]);
export type ReviewSubject = z.infer<typeof ReviewSubjectSchema>;

export const ReviewSummarySchema = z.object({
  id: z.string(),
  name: z.string(),
  due_on: z.string(),
  status: ReviewStatusSchema,
  overdue: z.boolean(),
  started_at: z.string(),
  progress: ReviewProgressSchema,
  subject: ReviewSubjectSchema.default("roles"),
});
export type ReviewSummary = z.infer<typeof ReviewSummarySchema>;
export const ReviewListSchema = listSchema(ReviewSummarySchema);

const EscalationSchema = z.object({
  at: z.string(),
  days_overdue: z.number(),
  open_items: z.number(),
  reviewers: z.array(PersonSchema),
  targets: z.array(PersonSchema),
});

/** One campaign with the progress of what the reader may see, per unit, and the escalations of late units. */
export const ReviewSchema = z.object({
  id: z.string(),
  name: z.string(),
  due_on: z.string(),
  status: ReviewStatusSchema,
  overdue: z.boolean(),
  owner: PersonSchema,
  units: z.array(z.object({ unit_id: z.string(), name: z.string(), includes_children: z.boolean() })),
  started_at: z.string(),
  closed_at: z.string().nullable(),
  progress: ReviewProgressSchema,
  by_unit: z.array(
    z.object({
      unit_id: z.string(),
      unit_name: z.string(),
      unit_path: z.array(z.string()),
      progress: ReviewProgressSchema,
      escalation: EscalationSchema.nullable(),
    }),
  ),
  can_manage: z.boolean(),
  subject: ReviewSubjectSchema.default("roles"),
});
export type Review = z.infer<typeof ReviewSchema>;

/** A visibility tag as a review names it. */
export const ReviewTagSchema = z.object({ code: z.string(), name: z.string() });
export type ReviewTag = z.infer<typeof ReviewTagSchema>;

/**
 * One assignment, or one visibility tag a person holds, to decide, as it stood when the campaign started, with its
 * decision once there is one; with the tags its person held then (TF-241 AC7).
 */
export const ReviewItemSchema = z.object({
  id: z.string(),
  review_id: z.string(),
  assignment_id: z.string(),
  principal_id: z.string(),
  principal_name: z.string(),
  role_id: z.string().nullable(),
  role_code: z.string().nullable(),
  role_name: z.string().nullable(),
  scope_kind: ScopeKindSchema.nullable(),
  /** The tag of a holding's item; null for a role assignment's (ADR-0057). */
  tag: ReviewTagSchema.nullable().default(null),
  /** The tags the person held when the campaign started, in code order. */
  held_tags: z.array(ReviewTagSchema).default([]),
  unit_id: z.string(),
  unit_name: z.string(),
  unit_path: z.array(z.object({ id: z.string(), name: z.string() })),
  decision: z
    .object({
      decision: ReviewDecisionSchema,
      comment: z.string().nullable(),
      decided_by: PersonSchema,
      decided_at: z.string(),
      /** The decision itself: the record its signature applies to (ADR-0051). */
      decision_id: z.string().nullable().optional(),
      /** The decision's signature, when the tenant's policy had it signed (TF-063). */
      signature: z
        .object({
          id: z.string(),
          record_version: z.number(),
          signer_name: z.string(),
          meaning: z.string(),
          method: z.string(),
          signed_at: z.string(),
        })
        .nullable()
        .optional(),
      assignment_removed: z.boolean(),
    })
    .nullable(),
  /** About the reader's own access: another reviewer decides it (ADR-0037). */
  yours: z.boolean(),
  /** Nobody but its own person may review its unit: the campaign's owner decides it. */
  with_owner: z.boolean(),
  /** The conflict rules this assignment takes part in, by name, from the report (ADR-0039). */
  conflicts: z.array(z.string()).default([]),
});
export type ReviewItem = z.infer<typeof ReviewItemSchema>;
export const ReviewItemListSchema = listSchema(ReviewItemSchema);

export type ItemState = "open" | "decided" | "all";

const reviews = "/api/v1/access-reviews";

export const fetchReviews = (query: string, signal?: AbortSignal) =>
  request(`${reviews}${query ? `?${query}` : ""}`, { schema: ReviewListSchema, ...(signal ? { signal } : {}) });

export const fetchReview = (id: string, signal?: AbortSignal) =>
  request(`${reviews}/${encodeURIComponent(id)}`, { schema: ReviewSchema, ...(signal ? { signal } : {}) });

export const fetchReviewItems = (id: string, state: ItemState, query: string, signal?: AbortSignal) =>
  request(`${reviews}/${encodeURIComponent(id)}/items?state=${state}${query ? `&${query}` : ""}`, {
    schema: ReviewItemListSchema,
    ...(signal ? { signal } : {}),
  });

export interface NewReview {
  name: string;
  due_on: string;
  units: { unit_id: string; include_children: boolean }[];
  subject?: ReviewSubject;
}

export const startReview = (body: NewReview) => request(reviews, { method: "POST", body, schema: ReviewSchema });

export const closeReview = (id: string) =>
  request(`${reviews}/${encodeURIComponent(id)}/close`, { method: "POST", schema: ReviewSchema });

/** Decides one item; with the signer's credentials when the tenant's policy has decisions signed (ADR-0051). */
export const decideItem = (
  review: string,
  item: string,
  decision: ReviewDecision,
  comment?: string,
  signature?: Record<string, unknown>,
) =>
  request(`${reviews}/${encodeURIComponent(review)}/items/${encodeURIComponent(item)}/decision`, {
    method: "POST",
    body: { decision, comment: comment ?? null, ...(signature ? { signature } : {}) },
    schema: ReviewItemSchema,
  });

export const ConfirmManySchema = z.object({ confirmed: z.number(), already_decided: z.number() });

export const confirmItems = (review: string, itemIds: string[], comment?: string) =>
  request(`${reviews}/${encodeURIComponent(review)}/confirm`, {
    method: "POST",
    body: { item_ids: itemIds, comment: comment ?? null },
    schema: ConfirmManySchema,
  });

/** A route value an action needs: a unit (its permission is checked there), a record (and its policy), or a plain value. */
export const ActionRouteValueSchema = z.object({
  name: z.string(),
  kind: z.enum(["unit", "record", "value"]),
  record_type: z.string().nullable(),
});
export type ActionRouteValue = z.infer<typeof ActionRouteValueSchema>;

/** An endpoint whose access can be explained (ADR-0039): its name, its summary in words, its module and what it needs. */
export const ExplainableActionSchema = z.object({
  name: z.string(),
  summary: z.string(),
  module: z.string(),
  method: z.string(),
  route: z.string(),
  permission: z.string().nullable(),
  route_values: z.array(ActionRouteValueSchema),
});
export type ExplainableAction = z.infer<typeof ExplainableActionSchema>;

export const fetchExplainableActions = (signal?: AbortSignal) =>
  request("/api/v1/access/actions", {
    schema: z.object({ data: z.array(ExplainableActionSchema) }),
    ...(signal ? { signal } : {}),
  });

const ExplainedUnitSchema = z.object({ id: z.string(), name: z.string() });

/** An assignment that granted the permission: the role and the scope (TF-039 AC2). */
export const ExplainedGrantSchema = z.object({
  assignment_id: z.string(),
  role_id: z.string(),
  role_code: z.string(),
  role_name: z.string(),
  scope_kind: z.string(),
  unit: ExplainedUnitSchema.nullable(),
  record_type: z.string().nullable(),
  record_id: z.string().nullable(),
});
export type ExplainedGrant = z.infer<typeof ExplainedGrantSchema>;

export const ExplainResultSchema = z.enum(["passed", "failed", "not_reached", "not_applicable"]);
export type ExplainResult = z.infer<typeof ExplainResultSchema>;

/** One check, explained: its result, its reason code, the status a failure answers, and the facts behind it. */
export const ExplainedCheckSchema = z.object({
  check: z.enum(["principal", "entitlement", "flags", "permission", "record_policy", "visibility_tags"]),
  result: ExplainResultSchema,
  reason: z.string(),
  status: z.number().nullable(),
  error_code: z.string().nullable(),
  message: z.string(),
  facts: z.record(z.string(), z.string()),
  granted_by: z.array(ExplainedGrantSchema),
});
export type ExplainedCheck = z.infer<typeof ExplainedCheckSchema>;

const ExplainedPersonSchema = z.object({
  id: z.string(),
  name: z.string(),
  type: z.string(),
  status: z.string(),
  home_unit: ExplainedUnitSchema.nullable(),
});

/** Why a person can or cannot perform an action: the checks in order, the outcome, and the status the API would answer. */
export const AccessExplanationSchema = z.object({
  principal: ExplainedPersonSchema,
  action: ExplainableActionSchema,
  acting_for: ExplainedPersonSchema.nullable(),
  allowed: z.boolean(),
  status: z.number(),
  checks: z.array(ExplainedCheckSchema),
});
export type AccessExplanation = z.infer<typeof AccessExplanationSchema>;

export interface ExplainQuestion {
  principal: string;
  action: string;
  values: Record<string, string>;
  actingFor?: string | undefined;
}

export function explainQuery(question: ExplainQuestion): string {
  const params = new URLSearchParams({ principal: question.principal, action: question.action });
  for (const [name, value] of Object.entries(question.values)) {
    if (value !== "") {
      params.set(name, value);
    }
  }
  if (question.actingFor) {
    params.set("acting_for", question.actingFor);
  }
  return params.toString();
}

export const explainAccess = (question: ExplainQuestion, signal?: AbortSignal) =>
  request(`/api/v1/access/explain?${explainQuery(question)}`, {
    schema: AccessExplanationSchema,
    ...(signal ? { signal } : {}),
  });

/** Segregation of duties (TF-036, ADR-0039): what a rule's side names, and what the rule does. */
export const ConflictSideKindSchema = z.enum(["permission", "role"]);
export type ConflictSideKind = z.infer<typeof ConflictSideKindSchema>;
export const ConflictModeSchema = z.enum(["block", "warn"]);
export type ConflictMode = z.infer<typeof ConflictModeSchema>;

const ConflictSideSchema = z.object({ kind: ConflictSideKindSchema, value: z.string(), name: z.string() });

export const ConflictRuleSchema = z.object({
  id: z.string(),
  name: z.string(),
  first: ConflictSideSchema,
  second: ConflictSideSchema,
  mode: ConflictModeSchema,
  reason: z.string(),
  owner_id: z.string(),
  owner_name: z.string().nullable(),
  updated_at: z.string(),
  version: z.number(),
});
export type ConflictRule = z.infer<typeof ConflictRuleSchema>;
export const ConflictRuleListSchema = listSchema(ConflictRuleSchema);

export interface ConflictRuleBody {
  name: string;
  first: { kind: ConflictSideKind; value: string };
  second: { kind: ConflictSideKind; value: string };
  mode: ConflictMode;
  reason: string;
  owner_id?: string | undefined;
}

const conflictRules = "/api/v1/conflict-rules";

export const fetchConflictRules = (query: string, signal?: AbortSignal) =>
  request(`${conflictRules}${query ? `?${query}` : ""}`, {
    schema: ConflictRuleListSchema,
    ...(signal ? { signal } : {}),
  });

export const createConflictRule = (body: ConflictRuleBody) =>
  request(conflictRules, { method: "POST", body, schema: ConflictRuleSchema });

export const updateConflictRule = (id: string, body: ConflictRuleBody) =>
  request(`${conflictRules}/${encodeURIComponent(id)}`, { method: "PUT", body, schema: ConflictRuleSchema });

export const deleteConflictRule = (id: string) =>
  request(`${conflictRules}/${encodeURIComponent(id)}`, { method: "DELETE" });

/** One grant of a conflict: the role, where it applies, and whether it is the person's own or delegated to them. */
export const ConflictGrantSchema = z.object({
  assignment_id: z.string().nullable(),
  delegation_id: z.string().nullable(),
  role: z.string(),
  scope_kind: z.string(),
  unit_id: z.string().nullable(),
  unit_name: z.string().nullable(),
  source: z.enum(["assignment", "delegation"]),
});
export type ConflictGrant = z.infer<typeof ConflictGrantSchema>;

/** One conflict of the report: the person, the rule, the two grants, and the unit both reach (none for the whole tenant). */
export const ConflictEntrySchema = z.object({
  id: z.string(),
  rule_id: z.string(),
  rule_name: z.string(),
  mode: ConflictModeSchema,
  principal_id: z.string(),
  principal_name: z.string(),
  first: ConflictGrantSchema,
  second: ConflictGrantSchema,
  unit_id: z.string().nullable(),
  unit_name: z.string().nullable(),
  computed_at: z.string(),
});
export type ConflictEntry = z.infer<typeof ConflictEntrySchema>;
export const ConflictEntryListSchema = listSchema(ConflictEntrySchema);

export const fetchConflicts = (query: string, signal?: AbortSignal) =>
  request(`/api/v1/conflict-report${query ? `?${query}` : ""}`, {
    schema: ConflictEntryListSchema,
    ...(signal ? { signal } : {}),
  });

export const ConflictRunSchema = z.object({
  id: z.string(),
  trigger: z.enum(["daily", "on_demand"]),
  rules: z.number(),
  conflicts: z.number(),
  computed_at: z.string(),
});
export type ConflictRun = z.infer<typeof ConflictRunSchema>;

export const fetchLatestConflictRun = (signal?: AbortSignal) =>
  request("/api/v1/conflict-report/runs/latest", {
    schema: z.object({ run: ConflictRunSchema.nullable() }),
    ...(signal ? { signal } : {}),
  });

export const runConflictReport = () =>
  request("/api/v1/conflict-report/runs", { method: "POST", schema: ConflictRunSchema });

/** A conflict a grant would create, before the grant: the rule, and the grant it meets on the other side. */
export const GrantConflictSchema = z.object({
  rule_id: z.string(),
  rule_name: z.string(),
  mode: ConflictModeSchema,
  reason: z.string(),
  with: ConflictGrantSchema,
  unit_id: z.string().nullable(),
  unit_name: z.string().nullable(),
});
export type GrantConflict = z.infer<typeof GrantConflictSchema>;

export const previewGrantConflicts = (
  body: { principal_id: string; role_id: string; scope_kind: ScopeKind; unit_id?: string },
  signal?: AbortSignal,
) =>
  request(`${assignments}/conflicts`, {
    method: "POST",
    body,
    schema: z.object({ data: z.array(GrantConflictSchema) }),
    ...(signal ? { signal } : {}),
  });
