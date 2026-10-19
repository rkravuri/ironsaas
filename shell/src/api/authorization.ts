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

export const fetchUnits = (signal?: AbortSignal) => request(units, { schema: UnitTreeSchema, ...(signal ? { signal } : {}) });

export const fetchKinds = (signal?: AbortSignal) => request("/api/v1/organisation-kinds", { schema: KindsSchema, ...(signal ? { signal } : {}) });

export const createUnit = (parentId: string, body: { name: string; code: string; kind: string }) =>
  request(`${units}/${encodeURIComponent(parentId)}/children`, { method: "POST", body, schema: UnitSchema });

export const renameUnit = (id: string, name: string) => request(`${units}/${encodeURIComponent(id)}/rename`, { method: "POST", body: { name }, schema: UnitSchema });

export const moveUnit = (id: string, parentId: string) =>
  request(`${units}/${encodeURIComponent(id)}/move`, { method: "POST", body: { parent_id: parentId }, schema: UnitSchema });

export const deactivateUnit = (id: string) => request(`${units}/${encodeURIComponent(id)}/deactivate`, { method: "POST", schema: UnitSchema });

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
export const PermissionSchema = z.object({ name: z.string(), module: z.string(), description: z.string(), risk: RiskSchema });
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
  changed: z.array(z.object({ code: z.string(), added: z.array(z.string()), removed: z.array(z.string()), fields: z.array(z.string()) })),
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

export const fetchRoles = (query: string, signal?: AbortSignal) => request(`${roles}${query ? `?${query}` : ""}`, { schema: RoleListSchema, ...(signal ? { signal } : {}) });

export const fetchRole = (id: string, signal?: AbortSignal) => request(`${roles}/${encodeURIComponent(id)}`, { schema: RoleSchema, ...(signal ? { signal } : {}) });

export const createRole = (code: string, input: RoleInput) => request(roles, { method: "POST", body: { code, ...input }, schema: RoleSchema });

export const updateRole = (id: string, input: RoleInput) => request(`${roles}/${encodeURIComponent(id)}`, { method: "PUT", body: input, schema: RoleSchema });

export const deleteRole = (id: string) => request(`${roles}/${encodeURIComponent(id)}`, { method: "DELETE" });

export const fetchPermissions = (signal?: AbortSignal) => request("/api/v1/permissions", { schema: PermissionsSchema, ...(signal ? { signal } : {}) });

export const exportRoles = () => request(`${roles}/export`, { schema: RoleExportSchema });

export const importRoles = (document: unknown, dryRun: boolean) => request(`${roles}/import`, { method: "POST", body: { dry_run: dryRun, document }, schema: ImportDiffSchema });

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

export const fetchAssignments = (query: string, signal?: AbortSignal) =>
  request(`${assignments}?${query}`, { schema: AssignmentListSchema, ...(signal ? { signal } : {}) });

export const grantRole = (body: { principal_id: string; role_id: string; scope_kind: ScopeKind; unit_id?: string }) =>
  request(assignments, { method: "POST", body, schema: AssignmentSchema });

export const removeAssignment = (id: string) => request(`${assignments}/${encodeURIComponent(id)}`, { method: "DELETE" });
