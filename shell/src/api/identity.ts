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
export const ActingForSchema = z.object({ id: z.string(), name: z.string(), duties: z.array(DutyNameSchema), until: z.string() });
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

/**
 * GET /api/v1/me: who is signed in, what they may do, their home unit, and when the session ends. `permissions` are
 * held for the whole tenant; `permissions_anywhere` at any scope, which the navigation follows (ADR-0031). While the
 * session acts for someone, both are that person's for the delegated duties (ADR-0037), and `acting_for` names them;
 * `delegations` are the active ones the person gives and holds.
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
  delegations: z.object({ given: z.array(ActiveDelegationSchema), received: z.array(ActiveDelegationSchema) }).optional(),
});
export type Me = z.infer<typeof MeSchema>;

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

/** GET /api/v1/users/{id}: one user with the home unit by name; a user outside the reader's scope answers 404. */
export const UserRecordSchema = UserSchema.omit({ home_unit_id: true }).extend({ home_unit: HomeUnitSchema.nullable() });
export type UserRecord = z.infer<typeof UserRecordSchema>;

/** GET /api/v1/me/access: the permissions held at one unit or record. */
export const AccessSchema = z.object({ scope: z.string(), permissions: z.array(z.string()) });
export type Access = z.infer<typeof AccessSchema>;

/**
 * Where to ask about access: a unit, or a unit with every unit below it (what changing an assignment to a unit and
 * below needs, ADR-0031), or a record with the unit it belongs to when the page knows it.
 */
export type AccessTarget = { unit: string; includeChildren?: boolean } | { recordType: string; recordId: string; unit?: string };

export const fetchMe = (signal?: AbortSignal) => request("/api/v1/me", { schema: MeSchema, ...(signal ? { signal } : {}) });

/** Act for someone whose delegation to you is active; answers /me as it is now. */
export const actFor = (principalId: string) => request("/api/v1/me/acting-for", { method: "POST", body: { principal_id: principalId }, schema: MeSchema });

/** Act as yourself again; answers /me. */
export const stopActing = () => request("/api/v1/me/acting-for", { method: "DELETE", schema: MeSchema });

/** A person as the delegate picker offers them: a name and a home unit, nothing more. */
export const DirectoryEntrySchema = z.object({ id: z.string(), display_name: z.string(), home_unit: z.string().nullable() });
export type DirectoryEntry = z.infer<typeof DirectoryEntrySchema>;

/** Active people whose name or login contains the search (two characters at least), for choosing a delegate. */
export const searchDirectory = (search: string, signal?: AbortSignal) =>
  request(`/api/v1/directory?search=${encodeURIComponent(search)}`, { schema: z.object({ data: z.array(DirectoryEntrySchema) }), ...(signal ? { signal } : {}) });

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

export const acceptInvitation = (token: string, password: string, displayName: string | undefined) =>
  request(`/auth/invitations/${encodeURIComponent(token)}/accept`, {
    method: "POST",
    body: { password, display_name: displayName },
  });

export const fetchUsers = (query: string, signal?: AbortSignal) =>
  request(`/api/v1/users${query ? `?${query}` : ""}`, { schema: UserListSchema, ...(signal ? { signal } : {}) });

export const fetchUser = (id: string, signal?: AbortSignal) =>
  request(`/api/v1/users/${encodeURIComponent(id)}`, { schema: UserRecordSchema, ...(signal ? { signal } : {}) });

/** POST /api/v1/users: the user created and the invitation link to pass on by hand until email delivery (S4). */
export const CreatedUserSchema = z.object({ user: UserSchema, invitation_link: z.string(), invitation_expires_at: z.string() });
export type CreatedUser = z.infer<typeof CreatedUserSchema>;

export interface NewUser {
  login_id: string;
  display_name: string;
  home_unit_id: string;
}

export const createUser = (body: NewUser) => request("/api/v1/users", { method: "POST", body, schema: CreatedUserSchema });
