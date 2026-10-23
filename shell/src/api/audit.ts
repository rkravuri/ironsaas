import { z } from "zod";
import { listSchema, request } from "./client";

/** One audit event as the trail answers it (TF-060), from the database or the archive (TF-064 AC2). */
export const AuditEventSchema = z.object({
  id: z.string(),
  event_type: z.string(),
  outcome: z.string(),
  actor_id: z.string(),
  actor_name: z.string().nullable().optional(),
  source: z.string(),
  target: z.string().nullable(),
  record_type: z.string().nullable(),
  record_id: z.string().nullable(),
  reason: z.string().nullable(),
  correlation_id: z.string().nullable(),
  details: z.record(z.string(), z.string()).nullable(),
  created_at: z.string(),
  created_by: z.string(),
  on_behalf_of_id: z.string().nullable().optional(),
  on_behalf_of_name: z.string().nullable().optional(),
  impersonator_id: z.string().nullable().optional(),
  impersonator_name: z.string().nullable().optional(),
  severity: z.string(),
  chain_position: z.number().nullable().optional(),
  archived: z.boolean().optional(),
});
export type AuditEvent = z.infer<typeof AuditEventSchema>;

export const AuditEventListSchema = listSchema(AuditEventSchema);

export const auditQueryKey = ["audit"] as const;

/** A page of the trail, with the list grammar's query (TF-064). */
export const fetchAuditEvents = (query: string, signal?: AbortSignal) =>
  request(`/api/v1/audit-events${query ? `?${query}` : ""}`, {
    schema: AuditEventListSchema,
    ...(signal ? { signal } : {}),
  });

/** A record's latest 200 events, newest first: the record template's History panel (TF-064 AC1). */
export const fetchRecordHistory = (recordType: string, recordId: string, signal?: AbortSignal) =>
  request(
    `/api/v1/audit-events?filter=record_type:eq:${encodeURIComponent(recordType)}&filter=record_id:eq:${encodeURIComponent(recordId)}&sort=-created_at&page_size=200`,
    { schema: AuditEventListSchema, ...(signal ? { signal } : {}) },
  );

export const AuditExportSchema = z.object({
  id: z.string(),
  rows: z.number(),
  created_at: z.string(),
  download_url: z.string(),
});
export type AuditExport = z.infer<typeof AuditExportSchema>;

/** Exports a search to CSV, kept as a file and handed over as a download; the export is itself audited. */
export const exportAuditEvents = (query: string) =>
  request(`/api/v1/audit-events/exports${query ? `?${query}` : ""}`, { method: "POST", schema: AuditExportSchema });

export const ArchivedEventsSchema = z.object({
  data: z.array(AuditEventSchema),
  batches_read: z.number(),
  truncated: z.boolean(),
});
export type ArchivedEvents = z.infer<typeof ArchivedEventsSchema>;

export interface ArchiveSearch {
  from: string;
  to: string;
  event_type?: string;
  actor_id?: string;
  impersonator_id?: string;
  record_type?: string;
  record_id?: string;
  source?: string;
}

/** Searches the archived trail by date range (TF-064 AC2). */
export const searchArchive = (search: ArchiveSearch, signal?: AbortSignal) => {
  const query = new URLSearchParams(
    Object.entries(search).filter(
      (entry): entry is [string, string] => typeof entry[1] === "string" && entry[1] !== "",
    ),
  );
  return request(`/api/v1/audit-events/archived?${query.toString()}`, {
    schema: ArchivedEventsSchema,
    ...(signal ? { signal } : {}),
  });
};

const VerificationSchema = z.object({
  result: z.string(),
  full: z.boolean(),
  from_position: z.number(),
  to_position: z.number(),
  events_checked: z.number(),
  problems: z.array(
    z.object({ kind: z.string(), position: z.number(), event_id: z.string().nullable(), message: z.string() }),
  ),
  restore_position: z.number().nullable(),
  restore_at: z.string().nullable(),
  verified_at: z.string(),
});
export type ChainVerification = z.infer<typeof VerificationSchema>;

export const AuditChainSchema = z.object({
  head_position: z.number().nullable(),
  head_hash: z.string().nullable(),
  head_sealed_at: z.string().nullable(),
  unsealed: z.number(),
  archived_up_to: z.number(),
  pruned_up_to: z.number(),
  seals: z.array(
    z.object({ first_position: z.number(), last_position: z.number(), last_hash: z.string(), sealed_at: z.string() }),
  ),
  anchored: z.boolean(),
  anchored_up_to: z.number().nullable(),
  latest_verification: VerificationSchema.nullable(),
  segment: z.number(),
  restores: z.array(
    z.object({
      segment: z.number(),
      start_position: z.number(),
      restore_position: z.number(),
      restore_at: z.string(),
      lost_from: z.number(),
      lost_to: z.number(),
      ticket: z.string(),
      reason: z.string(),
      accepted_by: z.string(),
      accepted_at: z.string(),
    }),
  ),
});
export type AuditChain = z.infer<typeof AuditChainSchema>;
export type ChainRestore = AuditChain["restores"][number];

/** Where the chain stands, with the control plane's latest verification and every accepted restore (TF-061, ADR-0050). */
export const fetchAuditChain = (signal?: AbortSignal) =>
  request("/api/v1/audit-chain", { schema: AuditChainSchema, ...(signal ? { signal } : {}) });

export const SignedInAsSchema = z.object({
  id: z.string(),
  name: z.string(),
  kind: z.enum(["operator", "break_glass", "person"]),
  field: z.enum(["impersonator_id", "actor_id"]),
});
export type SignedInAs = z.infer<typeof SignedInAsSchema>;

/**
 * Everyone who signed in as someone, or came in through break-glass, by name (TF-025, ADR-0049): the operators, the
 * break-glass accounts and the tenant's people, each with the field that finds their events.
 */
export const fetchImpersonators = (signal?: AbortSignal) =>
  request("/api/v1/audit-events/impersonators", {
    schema: z.object({ data: z.array(SignedInAsSchema) }),
    ...(signal ? { signal } : {}),
  });

export const RetentionClassSchema = z.object({
  code: z.string(),
  description: z.string(),
  default_days: z.number(),
  minimum_days: z.number(),
  maximum_days: z.number(),
  days: z.number(),
  chosen: z.boolean(),
  record_types: z.array(z.string()),
});
export type RetentionClass = z.infer<typeof RetentionClassSchema>;

export const fetchRetention = (signal?: AbortSignal) =>
  request("/api/v1/retention", {
    schema: z.object({ classes: z.array(RetentionClassSchema) }),
    ...(signal ? { signal } : {}),
  });

/** A class's days, within the operator's limits; below the minimum is refused (TF-066 AC3). */
export const chooseRetention = (code: string, days: number, reason: string) =>
  request(`/api/v1/retention/${encodeURIComponent(code)}`, {
    method: "PUT",
    body: { days, reason },
    schema: RetentionClassSchema,
  });

export const LegalHoldSchema = z.object({
  id: z.string(),
  scope: z.enum(["tenant", "record", "master"]),
  record_type: z.string().nullable(),
  record_id: z.string().nullable(),
  reason: z.string(),
  placed_at: z.string(),
  placed_by: z.string(),
  placed_by_name: z.string().nullable().optional(),
  released_at: z.string().nullable(),
  release_reason: z.string().nullable(),
});
export type LegalHold = z.infer<typeof LegalHoldSchema>;

export const fetchLegalHolds = (signal?: AbortSignal) =>
  request("/api/v1/legal-holds", {
    schema: z.object({ holds: z.array(LegalHoldSchema) }),
    ...(signal ? { signal } : {}),
  });

export const placeLegalHold = (hold: { scope: string; record_type?: string; record_id?: string; reason: string }) =>
  request("/api/v1/legal-holds", { method: "POST", body: hold, schema: LegalHoldSchema });

export const releaseLegalHold = (id: string, reason: string) =>
  request(`/api/v1/legal-holds/${id}/release`, { method: "POST", body: { reason }, schema: LegalHoldSchema });
