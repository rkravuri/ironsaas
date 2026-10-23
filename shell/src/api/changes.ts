import { z } from "zod";
import { listSchema, request } from "./client";
import type { SigningCredentials } from "./compliance";

/** The areas of sensitive configuration a tenant can ask a second administrator to approve (TF-068 AC1, ADR-0052). */
export const configurationAreas = [
  "roles",
  "single_sign_on",
  "retention",
  "mfa_and_password_policies",
  "legal_holds",
  "signing_policy",
] as const;
export type ConfigurationArea = (typeof configurationAreas)[number];

export const changeStates = ["pending", "applied", "rejected", "withdrawn", "expired"] as const;
export type ChangeState = (typeof changeStates)[number];

export const changeViews = ["to_approve", "mine", "all"] as const;
export type ChangeView = (typeof changeViews)[number];

/**
 * A change of sensitive configuration (TF-068): what it changes, its state before and after, who asked and why, where it
 * stands and who decided it, and whether the person looking may decide it and must sign to.
 */
export const PendingChangeSchema = z.object({
  id: z.string(),
  area: z.string(),
  kind: z.string(),
  subject: z.string(),
  title: z.string(),
  before: z.unknown(),
  after: z.unknown(),
  reason: z.string().nullable(),
  requested_by: z.string(),
  requester_name: z.string(),
  requested_at: z.string(),
  status: z.string(),
  expires_at: z.string(),
  reviews: z.number(),
  restarted_at: z.string().nullable(),
  restart_note: z.string().nullable(),
  decided_by: z.string().nullable(),
  decider_name: z.string().nullable(),
  decided_at: z.string().nullable(),
  decision_reason: z.string().nullable(),
  signature_id: z.string().nullable(),
  permission: z.string(),
  scope_unit_id: z.string().nullable(),
  may_decide: z.boolean(),
  signed: z.boolean(),
});
export type PendingChange = z.infer<typeof PendingChangeSchema>;

export const changesQueryKey = ["changes"] as const;

export const fetchChanges = (view: ChangeView, query: string, signal?: AbortSignal) =>
  request(`/api/v1/changes?view=${view}${query ? `&${query}` : ""}`, {
    schema: listSchema(PendingChangeSchema),
    ...(signal ? { signal } : {}),
  });

export const fetchChange = (id: string, signal?: AbortSignal) =>
  request(`/api/v1/changes/${encodeURIComponent(id)}`, { schema: PendingChangeSchema, ...(signal ? { signal } : {}) });

/** Approves or rejects a change someone else asked for, with a reason, and a signature when the tenant asks for one. */
export const decideChange = (
  id: string,
  decision: "approve" | "reject",
  reason: string,
  signature?: SigningCredentials,
) =>
  request(`/api/v1/changes/${encodeURIComponent(id)}/${decision}`, {
    method: "POST",
    body: { reason, ...(signature ? { signature } : {}) },
    schema: PendingChangeSchema,
  });

/** Takes back a change the person asked for, before anyone decides it. */
export const withdrawChange = (id: string) =>
  request(`/api/v1/changes/${encodeURIComponent(id)}/withdraw`, { method: "POST", schema: PendingChangeSchema });
