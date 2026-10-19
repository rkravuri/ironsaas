import { z } from "zod";
import { request } from "./client";

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
});
export type Unit = z.infer<typeof UnitSchema>;

/** GET /api/v1/organisation-units: the whole tree, depth first from the root, siblings by name. */
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

export const fetchUnits = (signal?: AbortSignal) => request(units, { schema: UnitTreeSchema, ...(signal ? { signal } : {}) });

export const fetchKinds = (signal?: AbortSignal) => request("/api/v1/organisation-kinds", { schema: KindsSchema, ...(signal ? { signal } : {}) });

export const createUnit = (parentId: string, body: { name: string; code: string; kind: string }) =>
  request(`${units}/${encodeURIComponent(parentId)}/children`, { method: "POST", body, schema: UnitSchema });

export const renameUnit = (id: string, name: string) => request(`${units}/${encodeURIComponent(id)}/rename`, { method: "POST", body: { name }, schema: UnitSchema });

export const moveUnit = (id: string, parentId: string) =>
  request(`${units}/${encodeURIComponent(id)}/move`, { method: "POST", body: { parent_id: parentId }, schema: UnitSchema });

export const deactivateUnit = (id: string) => request(`${units}/${encodeURIComponent(id)}/deactivate`, { method: "POST", schema: UnitSchema });
