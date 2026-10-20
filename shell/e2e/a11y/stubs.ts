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

/** A session as /me shows it, from now: idle in an hour and absolute in twelve unless a test moves them (TF-021). */
export const sessionFromNow = ({ idleIn = 60 * 60_000, endsIn = 12 * 60 * 60_000 }: { idleIn?: number; endsIn?: number } = {}) => ({
  id: "01a14e12-0000-7000-8000-000000000001",
  started_at: new Date(Date.now() - 20 * 60_000).toISOString(),
  expires_at: new Date(Date.now() + endsIn).toISOString(),
  idle_expires_at: new Date(Date.now() + Math.min(idleIn, endsIn)).toISOString(),
});

export const me = {
  id: "01a14e12-f3a8-7155-b5b6-17c3abceae77",
  login_id: "admin@acme.localhost",
  display_name: "Admin",
  type: "user",
  permissions: ["identity.users.read"],
  permissions_anywhere: ["identity.users.read"],
  session: sessionFromNow(),
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

/**
 * What /me/context answers (ADR-0034, ADR-0035): the enterprise plan, so role export and import show, and the flags
 * with no kill switch on; a test that needs another answer routes its own.
 */
export const context = {
  entitlements: {
    revision: 4,
    plan: "enterprise",
    confirmed_at: "2026-10-20T09:00:00Z",
    stale: false,
    modules: [] as unknown[],
    features: [{ code: "authorization.role_transfer", status: "enabled", reason: "licensed" }],
  },
  flags: {
    known: true,
    confirmed_at: "2026-10-20T09:00:00Z",
    values: [
      { key: "flags.example_release", type: "boolean", value: false, reason: "default" },
      { key: "kill.imports", type: "boolean", value: false, reason: "default" },
    ],
    kill_switches: [] as unknown[],
  },
};

/** The same answer with the platform operator's import kill switch on, as an administrator receives it. */
export const contextImportsKilled = {
  ...context,
  flags: {
    ...context.flags,
    values: context.flags.values.map((flag) => (flag.key === "kill.imports" ? { ...flag, value: true, reason: "targeting_match" } : flag)),
    kill_switches: [{ path: "imports", flag: "kill.imports", since: "2026-10-20T08:30:00Z", reason: "A fault in role import is being fixed; it is back by the afternoon." }],
  },
};

/** A signed-in administrator who may read users, and the users list from memory; every other call answers 401. */
export async function stubSignedIn(target: Page): Promise<void> {
  // Playwright tries the routes newest first, so the catch-all goes in first and the specific answers after it.
  await target.route("**/api/**", (route) => route.fulfill({ status: 401, contentType: "application/problem+json", body: JSON.stringify({ status: 401, error_code: "identity.unauthenticated" }) }));
  await target.route("**/api/v1/me", (route) => route.fulfill({ json: me }));
  await target.route("**/api/v1/me/context", (route) => route.fulfill({ json: context }));
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
  await target.route("**/api/v1/me/context", (route) => route.fulfill({ json: context }));
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
  "entitlements.subscription.read",
  "identity.users.manage",
  "identity.users.read",
  "settings.values.manage",
  "settings.values.read",
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

/**
 * The two people of the home page (D4): a tenant administrator at the root, and a child-unit administrator who reads
 * the users of North and the units below it, and nothing else.
 */
export const principals = {
  administrator: {
    ...me,
    display_name: "Ada Lovelace",
    permissions: administrator,
    permissions_anywhere: administrator,
    home_unit: { id: units[0].id, name: units[0].name, code: units[0].code },
  },
  child: {
    ...me,
    id: "01a14e12-f3a8-7155-b5b6-000000000002",
    login_id: "child-admin@acme.localhost",
    display_name: "Grace Hopper",
    permissions: [] as string[],
    permissions_anywhere: ["identity.users.read"],
    home_unit: { id: units[4].id, name: units[4].name, code: units[4].code },
  },
};
export type Who = keyof typeof principals;

/** Each one's own assignments, as GET /api/v1/role-assignments/mine answers them. */
const mine: Record<Who, unknown[]> = {
  administrator: [{ ...assignmentRow(10, roles[3], "tenant", null), principal_id: principals.administrator.id, principal_name: "Ada Lovelace" }],
  child: [{ ...assignmentRow(11, roles[4], "unit_with_children", units[4]), principal_id: principals.child.id, principal_name: "Grace Hopper" }],
};

/** The pages a person opened before, for the home page's Recently opened: kept per person in the browser. */
export const recentFor = (who: Who) => ({
  key: `iron.recent.${principals[who].id}`,
  pages: [
    { path: `/users/${users[1].id}`, titleKey: "modules.identity.users", module: "identity", label: users[1].display_name, at: 3 },
    { path: "/console/units", titleKey: "modules.console.units", module: "console", at: 2 },
    { path: "/users", titleKey: "modules.identity.users", module: "identity", at: 1 },
  ],
});

/**
 * One of the home page's people signed in, with the API from memory: /me, /me/access at the home unit and the person's
 * own assignments; the session's times can be moved to bring the warning on. Every other call answers 401.
 */
export async function stubHome(target: Page, who: Who, session = sessionFromNow()): Promise<void> {
  const principal = { ...principals[who], session };
  await target.route("**/api/**", (route) => route.fulfill({ status: 401, contentType: "application/problem+json", body: JSON.stringify({ status: 401, error_code: "identity.unauthenticated" }) }));
  await target.route("**/api/v1/me", (route) => route.fulfill({ json: principal }));
  await target.route("**/api/v1/me/context", (route) => route.fulfill({ json: context }));
  await target.route("**/api/v1/me/access?**", (route) => route.fulfill({ json: { scope: "unit", permissions: principal.permissions_anywhere } }));
  await target.route("**/api/v1/role-assignments/mine", (route) => route.fulfill({ json: { data: mine[who] } }));
}

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

/** The tenant's subscription as GET /api/v1/subscription answers it: the enterprise plan, one licensed module in its grace days, two features. */
export const subscription = {
  plan: "enterprise",
  revision: 4,
  confirmed_at: "2026-10-20T09:00:00Z",
  stale: false,
  grace_days: 30,
  modules: [
    {
      code: "sourcing",
      module: "sourcing",
      subscription_type: "per_person",
      status: "enabled",
      reason: "in_grace",
      starts_on: "2026-01-01",
      ends_on: "2026-10-15",
      grace_ends_on: "2026-11-14",
      seats: { limit: 50, assigned: 48, remaining: 2 },
    },
  ],
  features: [
    { code: "authorization.role_transfer", description: "Export the tenant's roles to a file and import roles from another tenant", module: "authz", licence: null, status: "enabled", reason: "licensed", starts_on: "2026-10-19", ends_on: null, grace_ends_on: null },
    { code: "sourcing.auctions", description: "Auctions with pre-bidding", module: "sourcing", licence: "sourcing", status: "disabled", reason: "not_licensed", starts_on: null, ends_on: null, grace_ends_on: null },
  ],
};

/** The console stubs plus the subscription. */
export async function stubSubscription(target: Page, body: unknown = subscription): Promise<void> {
  await stubConsole(target);
  await target.route("**/api/v1/subscription", (route) => route.fulfill({ json: body }));
}

const ravi = { id: "01a15100-0000-7000-8000-0000000000d1", name: "Ravi Ravuri" };
const notSet = { level: "default", unit: null, changed_by: null, changed_at: null };
const setting = (key: string, over: Record<string, unknown>) => ({
  key,
  module: "identity",
  type: "number",
  description: "",
  requirement: "",
  choices: null,
  minimum: null,
  maximum: null,
  whole_number: true,
  scopes: ["tenant"],
  default: null,
  value: null,
  source: notSet,
  set_here: false,
  ...over,
});

const idle = (value: number, source: Record<string, unknown>, setHere: boolean) =>
  setting("identity.session.idle_minutes", {
    description: "How long a session stays open without activity before it ends, in minutes. A new session takes the value; open sessions keep theirs.",
    requirement: "A whole number from 15 to 480.",
    minimum: 15,
    maximum: 480,
    default: 60,
    value,
    source,
    set_here: setHere,
  });

const absolute = setting("identity.session.absolute_hours", {
  description: "How long a session may last at most, active or not, in hours. A new session takes the value; open sessions keep theirs.",
  requirement: "A whole number from 1 to 24.",
  minimum: 1,
  maximum: 24,
  default: 12,
  value: 12,
});

const invitation = (value: number, source: Record<string, unknown>, setHere: boolean) =>
  setting("identity.invitation.valid_days", {
    description: "How long an invitation link stays valid, in days. An invitation takes the value of the unit it is sent into.",
    requirement: "A whole number from 1 to 30.",
    minimum: 1,
    maximum: 30,
    scopes: ["tenant", "unit"],
    default: 3,
    value,
    source,
    set_here: setHere,
  });

/** The tenant's settings as GET /api/v1/settings answers them: one set by Ravi, two at their defaults (ADR-0036). */
export const tenantSettings = {
  scope: "tenant",
  unit: null,
  data: [invitation(3, notSet, false), absolute, idle(30, { level: "tenant", unit: null, changed_by: ravi, changed_at: "2026-10-19T10:00:00Z" }, true)],
};

/** North's settings: the invitation validity inherited from Operations, the unit above it. */
export const northSettings = {
  scope: "unit",
  unit: { id: units[4].id, name: "North", path: "Acme Corporation / Operations / North" },
  data: [invitation(14, { level: "unit", unit: { id: units[3].id, name: "Operations", path: "Acme Corporation / Operations" }, changed_by: ravi, changed_at: "2026-10-19T11:00:00Z" }, false)],
};

/** The console stubs plus the settings of the tenant and of North. */
export async function stubSettings(target: Page): Promise<void> {
  await stubConsole(target);
  await target.route("**/api/v1/settings?**", (route) => {
    const url = new URL(route.request().url());
    return route.fulfill({ json: url.searchParams.get("scope") === "unit" ? northSettings : tenantSettings });
  });
}
