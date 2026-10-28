import { z } from "zod";
import { request } from "./client";

/** A rotation's re-encryption as the unit runs it (ADR-0054): the version every value moves to, and how far it has come. */
export const ReencryptionSchema = z.object({
  rotation_id: z.string(),
  to_version: z.number(),
  done: z.number(),
  total: z.number(),
  started_at: z.string().nullable(),
  finished_at: z.string().nullable(),
});
export type Reencryption = z.infer<typeof ReencryptionSchema>;

/** One of the tenant's keys, or its database password: its versions, its last and next rotation. */
export const TenantKeySchema = z.object({
  key: z.string(),
  version: z.number(),
  minimum_version: z.number(),
  rotated_at: z.string().nullable(),
  next_rotation_at: z.string(),
  rotation_days: z.number(),
  reencryption: ReencryptionSchema.nullable(),
});
export type TenantKey = z.infer<typeof TenantKeySchema>;

/** A rotation as the unit recorded it: who asked and why, the versions, and where it stands. */
export const KeyRotationSchema = z.object({
  id: z.string(),
  key: z.string(),
  trigger: z.string(),
  requested_by: z.string(),
  reason: z.string(),
  from_version: z.number(),
  to_version: z.number(),
  rotated_at: z.string(),
  done: z.number(),
  total: z.number(),
  finished_at: z.string().nullable(),
  retired_at: z.string().nullable(),
});
export type KeyRotation = z.infer<typeof KeyRotationSchema>;

/** GET /api/v1/keys: the tenant's keys, its latest rotations, and when the unit last heard from the platform. Read only. */
export const KeysSchema = z.object({
  keys: z.array(TenantKeySchema),
  rotations: z.array(KeyRotationSchema),
  asked_at: z.string().nullable(),
});
export type Keys = z.infer<typeof KeysSchema>;

export const fetchKeys = (signal?: AbortSignal) =>
  request("/api/v1/keys", { schema: KeysSchema, ...(signal ? { signal } : {}) });
