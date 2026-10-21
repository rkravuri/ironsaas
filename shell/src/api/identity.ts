import { z } from "zod";
import { listSchema, request } from "./client";

/** The session's times (TF-021): when it started, its absolute limit, and when it ends without activity. */
export const SessionSchema = z.object({
  id: z.string(),
  started_at: z.string().nullable().optional(),
  expires_at: z.string(),
  idle_expires_at: z.string(),
});

/** The home unit as /me names it (TF-034); the tree has the rest. */
export const HomeUnitSchema = z.object({ id: z.string(), name: z.string(), code: z.string() });

const DutyNameSchema = z.object({ code: z.string(), name: z.string() });

/** Whom the session acts for (ADR-0037): their rights for these duties, until then. */
export const ActingForSchema = z.object({
  id: z.string(),
  name: z.string(),
  duties: z.array(DutyNameSchema),
  until: z.string(),
});
export type ActingFor = z.infer<typeof ActingForSchema>;

/** An active delegation as /me shows it, either way. */
export const ActiveDelegationSchema = z.object({
  delegator_id: z.string(),
  delegator_name: z.string(),
  delegate_id: z.string(),
  delegate_name: z.string(),
  duties: z.array(DutyNameSchema),
  until: z.string(),
});
export type ActiveDelegation = z.infer<typeof ActiveDelegationSchema>;

/** One unit on a path from the root. */
export const UnitStepSchema = z.object({ id: z.string(), name: z.string() });

/** A unit the person may work for (ADR-0038): its depth, and its path from the root with the unit last. */
export const WorkingUnitChoiceSchema = z.object({
  id: z.string(),
  name: z.string(),
  code: z.string(),
  depth: z.number(),
  path: z.array(UnitStepSchema),
});
export type WorkingUnitChoice = z.infer<typeof WorkingUnitChoiceSchema>;

/**
 * GET /api/v1/me: who is signed in, what they may do, their home unit, and when the session ends. `permissions` are
 * held for the whole tenant; `permissions_anywhere` at any scope, which the navigation follows (ADR-0031). While the
 * session acts for someone, both are that person's for the delegated duties (ADR-0037), and `acting_for` names them;
 * `delegations` are the active ones the person gives and holds. `working_unit` is the unit new records and settings
 * follow, and lists keep to with the units below it, unless `all_units` is the consolidated view; `working_units` are
 * the units the person may choose (ADR-0038). A token has no working unit.
 */
export const MeSchema = z.object({
  id: z.string(),
  login_id: z.string(),
  display_name: z.string(),
  type: z.string(),
  permissions: z.array(z.string()),
  permissions_anywhere: z.array(z.string()),
  session: SessionSchema,
  home_unit: HomeUnitSchema.nullable().optional(),
  acting_for: ActingForSchema.nullable().optional(),
  delegations: z
    .object({ given: z.array(ActiveDelegationSchema), received: z.array(ActiveDelegationSchema) })
    .optional(),
  working_unit: HomeUnitSchema.nullable().optional(),
  all_units: z.boolean().optional(),
  working_units: z.object({ units: z.array(WorkingUnitChoiceSchema), all_units_available: z.boolean() }).optional(),
  /** When the password reaches the tenant's maximum age (TF-012); null while the age is off. */
  password_expires_at: z.string().nullable().optional(),
});
export type Me = z.infer<typeof MeSchema>;

/** One unit to work for, or all of them. */
export type WorkFor = { unitId: string } | { allUnits: true };

export const UserSchema = z.object({
  id: z.string(),
  login_id: z.string(),
  display_name: z.string(),
  type: z.string(),
  status: z.string(),
  email: z.string().nullable(),
  home_unit_id: z.string().nullable().optional(),
  last_login_at: z.string().nullable(),
  created_at: z.string(),
  updated_at: z.string(),
  version: z.number(),
});
export type User = z.infer<typeof UserSchema>;
export const UserListSchema = listSchema(UserSchema);
export type UserList = z.infer<typeof UserListSchema>;

/**
 * GET /api/v1/users/{id}: one user with the home unit by name, and until when their login is locked (TF-012), null when
 * it is not; a user outside the reader's scope answers 404.
 */
export const UserRecordSchema = UserSchema.omit({ home_unit_id: true }).extend({
  home_unit: HomeUnitSchema.nullable(),
  locked_until: z.string().nullable().optional(),
});
export type UserRecord = z.infer<typeof UserRecordSchema>;

/** GET /api/v1/me/access: the permissions held at one unit or record. */
export const AccessSchema = z.object({ scope: z.string(), permissions: z.array(z.string()) });
export type Access = z.infer<typeof AccessSchema>;

/**
 * Where to ask about access: a unit, or a unit with every unit below it (what changing an assignment to a unit and
 * below needs, ADR-0031), or a record with the unit it belongs to when the page knows it.
 */
export type AccessTarget =
  { unit: string; includeChildren?: boolean } | { recordType: string; recordId: string; unit?: string };

export const fetchMe = (signal?: AbortSignal) =>
  request("/api/v1/me", { schema: MeSchema, ...(signal ? { signal } : {}) });

/** Act for someone whose delegation to you is active; answers /me as it is now. */
export const actFor = (principalId: string) =>
  request("/api/v1/me/acting-for", { method: "POST", body: { principal_id: principalId }, schema: MeSchema });

/** Act as yourself again; answers /me. */
export const stopActing = () => request("/api/v1/me/acting-for", { method: "DELETE", schema: MeSchema });

/** Work for one of your units, or all of them (ADR-0038); answers /me. */
export const workFor = (choice: WorkFor) =>
  request("/api/v1/me/working-unit", {
    method: "POST",
    body: "allUnits" in choice ? { all_units: true } : { unit_id: choice.unitId },
    schema: MeSchema,
  });

/** A person as the delegate picker offers them (ADR-0037): a display name, a home unit's name, and the id to choose them by. */
export const DirectoryEntrySchema = z.object({
  id: z.string(),
  display_name: z.string(),
  home_unit: z.string().nullable(),
});
export type DirectoryEntry = z.infer<typeof DirectoryEntrySchema>;
export const DirectoryPageSchema = listSchema(DirectoryEntrySchema);

/** How many people the picker offers at once; more matches ask for more of the name. */
export const directoryPageSize = 20;

/** The first page of the active people whose name or login contains the search (two characters at least). */
export const searchDirectory = (search: string, signal?: AbortSignal) =>
  request(`/api/v1/directory?search=${encodeURIComponent(search)}&page_size=${directoryPageSize}`, {
    schema: DirectoryPageSchema,
    ...(signal ? { signal } : {}),
  });

export function accessQuery(target: AccessTarget): string {
  const params = new URLSearchParams();
  if ("recordType" in target) {
    params.set("record_type", target.recordType);
    params.set("record_id", target.recordId);
  }
  if (target.unit !== undefined) {
    params.set("unit", target.unit);
  }
  if ("includeChildren" in target && target.includeChildren === true) {
    params.set("include_children", "true");
  }
  return params.toString();
}

export const fetchAccess = (target: AccessTarget, signal?: AbortSignal) =>
  request(`/api/v1/me/access?${accessQuery(target)}`, { schema: AccessSchema, ...(signal ? { signal } : {}) });

export const login = (loginId: string, password: string) =>
  request("/auth/login", { method: "POST", body: { login_id: loginId, password } });

export const logout = () => request("/auth/logout", { method: "POST" });

/** The token from the link's fragment, in the body: never in a path an access log or a trace keeps (ADR-0046). */
export const acceptInvitation = (token: string, password: string, displayName: string | undefined) =>
  request("/auth/invitations/accept", {
    method: "POST",
    body: { token, password, display_name: displayName },
  });

export const fetchUsers = (query: string, signal?: AbortSignal) =>
  request(`/api/v1/users${query ? `?${query}` : ""}`, { schema: UserListSchema, ...(signal ? { signal } : {}) });

export const fetchUser = (id: string, signal?: AbortSignal) =>
  request(`/api/v1/users/${encodeURIComponent(id)}`, { schema: UserRecordSchema, ...(signal ? { signal } : {}) });

/** POST /api/v1/users: the user created and the invitation link to pass on by hand until email delivery (S4). */
export const CreatedUserSchema = z.object({
  user: UserSchema,
  invitation_link: z.string(),
  invitation_expires_at: z.string(),
});
export type CreatedUser = z.infer<typeof CreatedUserSchema>;

export interface NewUser {
  login_id: string;
  display_name: string;
  home_unit_id: string;
}

export const createUser = (body: NewUser) =>
  request("/api/v1/users", { method: "POST", body, schema: CreatedUserSchema });

/** GET /auth/password-policy: the tenant's password rules (TF-012), anonymous, for every form that sets a password. */
export const PasswordPolicySchema = z.object({
  minimum_length: z.number(),
  maximum_length: z.number(),
  common_check: z.boolean(),
  history_depth: z.number(),
  maximum_age_days: z.number(),
  warning_days: z.number(),
});
export type PasswordPolicy = z.infer<typeof PasswordPolicySchema>;

export const fetchPasswordPolicy = (signal?: AbortSignal) =>
  request("/auth/password-policy", { schema: PasswordPolicySchema, ...(signal ? { signal } : {}) });

/** Sets a new password with a recovery link (TF-017): once, within 15 minutes; every session of the person ends. */
export const recoverPassword = (token: string, password: string) =>
  request("/auth/recovery", { method: "POST", body: { token, password } });

/** Changes your own password with the current one; the other sessions end unless you say not to (TF-017 AC3). */
export const changePassword = (currentPassword: string, newPassword: string, endOtherSessions: boolean) =>
  request("/api/v1/me/password", {
    method: "POST",
    body: { current_password: currentPassword, new_password: newPassword, end_other_sessions: endOtherSessions },
  });

/** One of your security events (TF-019): what, when, by whom (yourself, an administrator, the operator, the system), why. */
export const SecurityEventSchema = z.object({
  id: z.string(),
  kind: z.string(),
  occurred_at: z.string(),
  actor: z.string(),
  actor_name: z.string().nullable(),
  reason: z.string().nullable(),
});
export type SecurityEvent = z.infer<typeof SecurityEventSchema>;
export const SecurityEventListSchema = listSchema(SecurityEventSchema);

/** How many security events My profile shows at once, newest first. */
export const securityEventsPageSize = 50;

export const fetchSecurityEvents = (signal?: AbortSignal) =>
  request(`/api/v1/me/security-events?page_size=${securityEventsPageSize}`, {
    schema: SecurityEventListSchema,
    ...(signal ? { signal } : {}),
  });

/** A recovery link an administrator issued, shown once: its path and when it stops working. */
export const RecoveryLinkSchema = z.object({ recovery_link: z.string(), expires_at: z.string() });
export type RecoveryLink = z.infer<typeof RecoveryLinkSchema>;

/** A person reactivated, and a new invitation's link when they never set a password. */
export const ReactivatedUserSchema = z.object({
  user: UserSchema,
  invitation_link: z.string().nullable(),
  invitation_expires_at: z.string().nullable(),
});
export type ReactivatedUser = z.infer<typeof ReactivatedUserSchema>;

const userAction = (id: string, action: string) => `/api/v1/users/${encodeURIComponent(id)}/${action}`;

export const unlockUser = (id: string) =>
  request(userAction(id, "unlock"), { method: "POST", schema: UserRecordSchema });

export const issueRecoveryLink = (id: string) =>
  request(userAction(id, "recovery-link"), { method: "POST", schema: RecoveryLinkSchema });

export const deactivateUser = (id: string) =>
  request(userAction(id, "deactivate"), { method: "POST", schema: UserRecordSchema });

export const reactivateUser = (id: string) =>
  request(userAction(id, "reactivate"), { method: "POST", schema: ReactivatedUserSchema });
