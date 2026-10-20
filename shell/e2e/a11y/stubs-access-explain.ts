import type { Page } from "@playwright/test";
import { administrator, me, record, roles, sessionFromNow, stubAccess, units } from "./stubs";

const grace = {
  id: "01a17e00-0000-7000-8000-0000000000a2",
  name: "Grace Hopper",
};
const olga = { id: "01a17e00-0000-7000-8000-0000000000a3", name: "Olga Owner" };
const north = units.find((unit) => unit.code === "operations-north")!;
const operations = units.find((unit) => unit.code === "operations")!;
const userReader = roles.find((role) => role.code === "user-reader")!;
const operationsLead = roles.find((role) => role.code === "operations-lead")!;
const listOf = <T>(data: T[]) => ({
  data,
  meta: {
    page_number: 1,
    page_size: 50,
    total_count: data.length,
    total_pages: 1,
    has_previous: false,
    has_next: false,
    page_size_capped: false,
    max_page_size: 200,
  },
  links: { self: "x", first: "x", previous: null, next: null, last: "x" },
});

const explainer = [...administrator, "authz.access.explain", "authz.sod.read", "authz.sod.manage"];

const actions = [
  {
    name: "ImportRoles",
    summary: "Import roles from another tenant's export.",
    module: "authz",
    method: "POST",
    route: "/api/v1/roles/import",
    permission: "authz.roles.manage",
    route_values: [],
  },
  {
    name: "RenameOrganisationUnit",
    summary: "Rename an organisation unit.",
    module: "authz",
    method: "POST",
    route: "/api/v1/organisation-units/{id}/rename",
    permission: "authz.units.manage",
    route_values: [{ name: "id", kind: "unit", record_type: null }],
  },
  {
    name: "ListUsers",
    summary: "List the users and service accounts.",
    module: "identity",
    method: "GET",
    route: "/api/v1/users",
    permission: "identity.users.read",
    route_values: [],
  },
];

const principal = {
  id: grace.id,
  name: grace.name,
  type: "user",
  status: "active",
  home_unit: { id: north.id, name: north.name },
};
const check = (name: string, result: string, reason: string, extra: Record<string, unknown> = {}) => ({
  check: name,
  result,
  reason,
  status: null,
  error_code: null,
  message: "",
  facts: {},
  granted_by: [],
  ...extra,
});

/** Role import needs authz.roles.manage for the whole organisation; Grace manages North and the units below it only. */
const denial = {
  principal,
  action: actions[0],
  acting_for: null,
  allowed: false,
  status: 403,
  checks: [
    check("principal", "passed", "active"),
    check("entitlement", "passed", "entitled", {
      facts: { subject: "authorization.role_transfer" },
    }),
    check("flags", "passed", "flags_allow", {
      facts: { flag: "kill.imports" },
    }),
    check("permission", "failed", "not_granted", {
      status: 403,
      error_code: "authz.permission.denied",
      facts: {
        permission: "authz.roles.manage",
        permission_name: "Create, change and delete tenant roles",
        scope: "tenant",
      },
    }),
    check("record_policy", "not_reached", "not_reached"),
  ],
};

const allowance = {
  principal,
  action: actions[1],
  acting_for: null,
  allowed: true,
  status: 200,
  checks: [
    check("principal", "passed", "active"),
    check("entitlement", "not_applicable", "not_licensed"),
    check("flags", "not_applicable", "no_flags"),
    check("permission", "passed", "granted", {
      facts: {
        permission: "authz.units.manage",
        permission_name: "Create, rename, move and deactivate organisation units",
        scope: "unit",
        unit_id: north.id,
        unit: north.name,
      },
      granted_by: [
        {
          assignment_id: "01a17e00-0000-7000-8000-0000000000f1",
          role_id: operationsLead.id,
          role_code: operationsLead.code,
          role_name: operationsLead.name,
          scope_kind: "unit_with_children",
          unit: { id: operations.id, name: operations.name },
          record_type: null,
          record_id: null,
        },
      ],
    }),
    check("record_policy", "not_applicable", "no_record"),
  ],
};

const rules = [
  {
    id: "01a17e00-0000-7000-8000-0000000000c1",
    name: "Shape the tree or read its people",
    first: {
      kind: "permission",
      value: "authz.units.manage",
      name: "Create, rename, move and deactivate organisation units",
    },
    second: { kind: "role", value: userReader.id, name: userReader.name },
    mode: "block",
    reason: "Who shapes a unit should not also read the people in it.",
    owner_id: olga.id,
    owner_name: olga.name,
    updated_at: "2026-10-20T08:00:00Z",
    version: 1,
  },
  {
    id: "01a17e00-0000-7000-8000-0000000000c2",
    name: "Roles and their holders",
    first: {
      kind: "permission",
      value: "authz.roles.manage",
      name: "Create, change and delete tenant roles",
    },
    second: {
      kind: "permission",
      value: "authz.assignments.manage",
      name: "Assign roles to principals and remove assignments",
    },
    mode: "warn",
    reason: "Who writes a role should not also hand it out.",
    owner_id: olga.id,
    owner_name: olga.name,
    updated_at: "2026-10-20T08:00:00Z",
    version: 1,
  },
];

const grant = (
  role: string,
  scope: string,
  unit: (typeof units)[number] | null,
  source: "assignment" | "delegation" = "assignment",
) => ({
  assignment_id: source === "assignment" ? "01a17e00-0000-7000-8000-0000000000f2" : null,
  delegation_id: source === "delegation" ? "01a17e00-0000-7000-8000-0000000000f3" : null,
  role,
  scope_kind: scope,
  unit_id: unit?.id ?? null,
  unit_name: unit?.name ?? null,
  source,
});

const entries = [
  {
    id: "01a17e00-0000-7000-8000-0000000000d1",
    rule_id: rules[1].id,
    rule_name: rules[1].name,
    mode: "warn",
    principal_id: grace.id,
    principal_name: grace.name,
    first: grant("Role writer", "tenant", null),
    second: grant("Operations lead", "unit_with_children", operations, "delegation"),
    unit_id: operations.id,
    unit_name: operations.name,
    computed_at: "2026-10-20T06:00:00Z",
  },
  {
    id: "01a17e00-0000-7000-8000-0000000000d2",
    rule_id: rules[1].id,
    rule_name: rules[1].name,
    mode: "warn",
    principal_id: olga.id,
    principal_name: olga.name,
    first: grant("Role writer", "tenant", null),
    second: grant("Access granter", "tenant", null),
    unit_id: null,
    unit_name: null,
    computed_at: "2026-10-20T06:00:00Z",
  },
];

const run = {
  id: "01a17e00-0000-7000-8000-0000000000e1",
  trigger: "daily",
  rules: rules.length,
  conflicts: entries.length,
  computed_at: "2026-10-20T06:00:00Z",
};

/** What a grant of User reader to the record's user meets: the rule that blocks it, at the unit both reach. */
const grantConflicts = {
  data: [
    {
      rule_id: rules[0].id,
      rule_name: rules[0].name,
      mode: "block",
      reason: rules[0].reason,
      with: grant(operationsLead.name, "unit_with_children", operations),
      unit_id: north.id,
      unit_name: north.name,
    },
  ],
};

/**
 * An administrator who may explain access and keep the conflict rules (ADR-0039), on the A2 stubs: the explainable
 * actions, an explanation for each answer the page shows, the directory, the rules, the report and a grant's preview.
 */
export async function stubExplainAndConflicts(target: Page): Promise<void> {
  await stubAccess(target);
  await target.route("**/api/v1/me", (route) =>
    route.fulfill({
      json: {
        ...me,
        session: sessionFromNow(),
        permissions: explainer,
        permissions_anywhere: explainer,
        home_unit: {
          id: units[0].id,
          name: units[0].name,
          code: units[0].code,
        },
      },
    }),
  );
  await target.route("**/api/v1/me/access?**", (route) =>
    route.fulfill({ json: { scope: "unit", permissions: explainer } }),
  );
  await target.route("**/api/v1/directory?**", (route) =>
    route.fulfill({
      json: listOf([
        { id: grace.id, display_name: grace.name, home_unit: north.name },
        { id: olga.id, display_name: olga.name, home_unit: "Finance" },
      ]),
    }),
  );
  await target.route("**/api/v1/access/actions", (route) => route.fulfill({ json: { data: actions } }));
  await target.route("**/api/v1/access/explain?**", (route) => {
    const action = new URL(route.request().url()).searchParams.get("action");
    return route.fulfill({
      json: action === "ImportRoles" ? denial : allowance,
    });
  });
  await target.route("**/api/v1/conflict-rules?**", (route) => route.fulfill({ json: listOf(rules) }));
  await target.route("**/api/v1/conflict-report?**", (route) => route.fulfill({ json: listOf(entries) }));
  await target.route("**/api/v1/conflict-report/runs/latest", (route) => route.fulfill({ json: { run } }));
  await target.route("**/api/v1/conflict-report/runs", (route) =>
    route.fulfill({ status: 201, json: { ...run, trigger: "on_demand" } }),
  );
  await target.route("**/api/v1/role-assignments/conflicts", (route) => route.fulfill({ json: grantConflicts }));
}

export { grace, north, record, userReader };
