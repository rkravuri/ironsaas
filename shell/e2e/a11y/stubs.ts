import type { Page } from "@playwright/test";

/** Twenty users, as the Identity API lists them, so the real Users page runs against the preview without a stack. */
export const users = Array.from({ length: 20 }, (_, index) => {
  const n = index + 1;
  const names = ["Ada Lovelace", "Grace Hopper", "Linus Pauling", "Marie Curie", "Alan Turing"];
  return {
    id: `01a14e12-0000-7000-8000-${String(n).padStart(12, "0")}`,
    login_id: `user${String(n).padStart(2, "0")}@acme.localhost`,
    display_name: `${names[index % names.length] ?? "Someone"} ${n}`,
    type: n % 7 === 0 ? "service_account" : "user",
    status: n % 5 === 0 ? "invited" : "active",
    email: `user${n}@example.com`,
    last_login_at: n % 3 === 0 ? null : "2026-10-17T08:15:00Z",
    created_at: "2026-10-01T09:00:00Z",
    updated_at: "2026-10-02T09:00:00Z",
    version: 1,
  };
});

export const me = {
  id: "01a14e12-f3a8-7155-b5b6-17c3abceae77",
  login_id: "admin@acme.localhost",
  display_name: "Admin",
  type: "user",
  permissions: ["identity.users.read"],
  permissions_anywhere: ["identity.users.read"],
  session: { id: "01a14e12-0000-7000-8000-000000000001", expires_at: "2026-10-19T08:00:00Z", idle_expires_at: "2026-10-19T07:00:00Z" },
};

function page(url: string) {
  const params = new URL(url).searchParams;
  const pageNumber = Math.max(1, Number(params.get("page_number") ?? "1"));
  const pageSize = Math.max(1, Number(params.get("page_size") ?? "50"));
  const start = (pageNumber - 1) * pageSize;
  const totalPages = Math.max(1, Math.ceil(users.length / pageSize));
  return {
    data: users.slice(start, start + pageSize),
    meta: { page_number: pageNumber, page_size: pageSize, total_count: users.length, total_pages: totalPages, has_previous: pageNumber > 1, has_next: pageNumber < totalPages, page_size_capped: false, max_page_size: 200 },
    links: { self: url, first: url, previous: null, next: null, last: url },
  };
}

/** A signed-in administrator who may read users, and the users list from memory; every other call answers 401. */
export async function stubSignedIn(target: Page): Promise<void> {
  // Playwright tries the routes newest first, so the catch-all goes in first and the specific answers after it.
  await target.route("**/api/**", (route) => route.fulfill({ status: 401, contentType: "application/problem+json", body: JSON.stringify({ status: 401, error_code: "identity.unauthenticated" }) }));
  await target.route("**/api/v1/me", (route) => route.fulfill({ json: me }));
  await target.route("**/api/v1/users**", (route) => route.fulfill({ json: page(route.request().url()) }));
  // One user by id, as the record page asks; the list's route above answers the rest.
  await target.route("**/api/v1/users/*", (route) => {
    const user = users.find((candidate) => route.request().url().endsWith(`/${candidate.id}`));
    return user
      ? route.fulfill({ json: { ...user, home_unit: null } })
      : route.fulfill({ status: 404, contentType: "application/problem+json", body: JSON.stringify({ status: 404, error_code: "identity.user.not_found" }) });
  });
}

const unitRow = (n: number, name: string, code: string, kind: string, parent: string | null, depth: number, status: "active" | "inactive" = "active") => ({
  id: `01a15100-0000-7000-8000-${String(n).padStart(12, "0")}`,
  name,
  code,
  kind,
  parent_id: parent === null ? null : `01a15100-0000-7000-8000-${parent.padStart(12, "0")}`,
  depth,
  status,
  created_at: "2026-10-19T09:00:00Z",
  updated_at: "2026-10-19T09:30:00Z",
  version: 1,
});

/** An organisation tree as the Authorization API lists it: depth first from the root, siblings by name. */
export const units = [
  unitRow(1, "Acme Corporation", "acme", "root", null, 0),
  unitRow(6, "Closed office", "closed-office", "unit", "1", 1, "inactive"),
  unitRow(2, "Finance", "finance", "unit", "1", 1),
  unitRow(3, "Operations", "operations", "unit", "1", 1),
  unitRow(4, "North", "operations-north", "unit", "3", 2),
  unitRow(5, "South", "operations-south", "unit", "3", 2),
  unitRow(7, "Sales", "sales", "unit", "1", 1),
  unitRow(8, "Enterprise", "sales-enterprise", "unit", "7", 2),
  unitRow(9, "Partners", "sales-partners", "unit", "7", 2),
];

export const kinds = {
  root_kind: "root",
  data: [
    { code: "root", name: "Root", child_kinds: ["unit"] },
    { code: "unit", name: "Unit", child_kinds: ["unit"] },
  ],
};

/** A signed-in administrator who may read and shape the organisation tree, with the tree and its kinds from memory. */
export async function stubConsole(target: Page): Promise<void> {
  await target.route("**/api/**", (route) => route.fulfill({ status: 401, contentType: "application/problem+json", body: JSON.stringify({ status: 401, error_code: "identity.unauthenticated" }) }));
  await target.route("**/api/v1/me", (route) =>
    route.fulfill({ json: { ...me, permissions: administrator, permissions_anywhere: administrator, home_unit: { id: units[0].id, name: units[0].name, code: units[0].code } } }),
  );
  await target.route("**/api/v1/me/access?**", (route) => route.fulfill({ json: { scope: "unit", permissions: administrator } }));
  await target.route("**/api/v1/organisation-units", (route) => route.fulfill({ json: { data: units } }));
  await target.route("**/api/v1/organisation-kinds", (route) => route.fulfill({ json: kinds }));
}

/** The tenant administrator's permissions, as /me lists them. */
const administrator = [
  "authz.assignments.manage",
  "authz.assignments.read",
  "authz.roles.manage",
  "authz.roles.read",
  "authz.units.manage",
  "authz.units.read",
  "identity.users.manage",
  "identity.users.read",
];

/** The permission catalogue as GET /api/v1/permissions answers it. */
export const catalogue = [
  { name: "authz.assignments.manage", module: "authz", description: "Assign roles to principals and remove assignments", risk: "high" },
  { name: "authz.assignments.read", module: "authz", description: "See who holds which role, and where", risk: "low" },
  { name: "authz.roles.manage", module: "authz", description: "Create, change and delete tenant roles", risk: "high" },
  { name: "authz.roles.read", module: "authz", description: "List and view roles and their permissions", risk: "low" },
  { name: "authz.units.manage", module: "authz", description: "Create, rename, move and deactivate organisation units", risk: "high" },
  { name: "authz.units.read", module: "authz", description: "View the organisation units of this tenant", risk: "low" },
  { name: "identity.users.manage", module: "identity", description: "Invite, change, disable and re-enable users", risk: "high" },
  { name: "identity.users.read", module: "identity", description: "List and view users and service accounts", risk: "low" },
];

const roleRow = (n: number, code: string, name: string, description: string, system: boolean, type: "user" | "service" | "any", permissions: string[]) => ({
  id: `01a15200-0000-7000-8000-${String(n).padStart(12, "0")}`,
  code,
  name,
  description,
  is_system: system,
  principal_type: type,
  permissions,
  created_at: "2026-10-19T09:00:00Z",
  updated_at: "2026-10-19T09:30:00Z",
  version: 1,
});

/** The roles of a tenant: the three system roles and two of its own. */
export const roles = [
  roleRow(4, "operations-lead", "Operations lead", "Shapes the Operations part of the tree and its people.", false, "user", ["authz.units.manage", "authz.units.read", "identity.users.read"]),
  roleRow(2, "read-only", "Read only", "Read permissions only.", true, "user", catalogue.filter((p) => p.name.endsWith(".read")).map((p) => p.name)),
  roleRow(3, "service-default", "Service default", "Read permissions for service accounts.", true, "service", catalogue.filter((p) => p.name.endsWith(".read")).map((p) => p.name)),
  roleRow(1, "tenant-administrator", "Tenant administrator", "Every permission of the catalogue.", true, "any", catalogue.map((p) => p.name)),
  roleRow(5, "user-reader", "User reader", "Lists the people of a unit.", false, "user", ["identity.users.read"]),
];

const envelope = <T>(url: string, rows: T[]) => ({
  data: rows,
  meta: { page_number: 1, page_size: 50, total_count: rows.length, total_pages: 1, has_previous: false, has_next: false, page_size_capped: false, max_page_size: 200 },
  links: { self: url, first: url, previous: null, next: null, last: url },
});

/** The user the record page shows, with the home unit by name. */
export const record = { ...users[0], home_unit: { id: units[3].id, name: units[3].name, code: units[3].code } };

const assignmentRow = (n: number, role: (typeof roles)[number], scope: string, unit: (typeof units)[number] | null) => ({
  id: `01a15300-0000-7000-8000-${String(n).padStart(12, "0")}`,
  principal_id: record.id,
  principal_name: record.display_name,
  role_id: role.id,
  role_code: role.code,
  role_name: role.name,
  scope_kind: scope,
  unit_id: unit?.id ?? null,
  record_type: null,
  record_id: null,
  unit_path: unit === null ? [] : unit.id === units[4].id ? [units[0], units[3], units[4]].map(({ id, name }) => ({ id, name })) : [units[0], units[3]].map(({ id, name }) => ({ id, name })),
  created_at: "2026-10-19T10:00:00Z",
});

export const assignments = [
  assignmentRow(1, roles[0], "unit_with_children", units[3]),
  assignmentRow(2, roles[4], "unit", units[4]),
  assignmentRow(3, roles[1], "tenant", null),
];

/** The console stubs plus the roles, the catalogue, one user record and its assignments: the A2 pages without a stack. */
export async function stubAccess(target: Page): Promise<void> {
  await stubConsole(target);
  await target.route("**/api/v1/permissions", (route) => route.fulfill({ json: { data: catalogue } }));
  await target.route("**/api/v1/roles**", (route) => route.fulfill({ json: envelope(route.request().url(), roles) }));
  for (const role of roles) {
    await target.route(`**/api/v1/roles/${role.id}`, (route) => route.fulfill({ json: role }));
  }
  await target.route("**/api/v1/role-assignments?**", (route) => route.fulfill({ json: envelope(route.request().url(), assignments) }));
  await target.route("**/api/v1/users**", (route) => route.fulfill({ json: page(route.request().url()) }));
  await target.route(`**/api/v1/users/${record.id}`, (route) => route.fulfill({ json: record }));
}
