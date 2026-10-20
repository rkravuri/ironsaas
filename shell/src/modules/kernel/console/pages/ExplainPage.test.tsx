import { afterEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, within } from "@testing-library/react";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { ExplainPage } from "./ExplainPage";

const unit = (id: string, name: string, parent: string | null) => ({
  id,
  name,
  code: id,
  kind: parent === null ? "root" : "unit",
  parent_id: parent,
  depth: parent === null ? 0 : 1,
  status: "active",
  created_at: "2026-10-19T08:00:00Z",
  updated_at: "2026-10-19T08:00:00Z",
  version: 1,
});

const actions = {
  data: [
    { name: "ImportRoles", summary: "Import roles from another tenant.", module: "authz", method: "POST", route: "/api/v1/roles/import", permission: "authz.roles.manage", route_values: [] },
    { name: "RenameOrganisationUnit", summary: "Rename a unit.", module: "authz", method: "POST", route: "/api/v1/organisation-units/{id}/rename", permission: "authz.units.manage", route_values: [{ name: "id", kind: "unit", record_type: null }] },
  ],
};

const grace = { id: "p1", name: "Grace Hopper", type: "user", status: "active", home_unit: { id: "north", name: "North" } };
const check = (name: string, result: string, reason: string, extra: Record<string, unknown> = {}) => ({ check: name, result, reason, status: null, error_code: null, message: "", facts: {}, granted_by: [], ...extra });

const denial = {
  principal: grace,
  action: actions.data[0],
  acting_for: null,
  allowed: false,
  status: 403,
  checks: [
    check("principal", "passed", "active"),
    check("entitlement", "failed", "not_licensed", { status: 403, error_code: "entitlements.feature_disabled", facts: { subject: "authorization.role_transfer" } }),
    check("flags", "not_reached", "not_reached"),
    check("permission", "not_reached", "not_reached"),
    check("record_policy", "not_reached", "not_reached"),
  ],
};

const allowance = {
  principal: grace,
  action: actions.data[1],
  acting_for: null,
  allowed: true,
  status: 200,
  checks: [
    check("principal", "passed", "active"),
    check("entitlement", "not_applicable", "not_licensed"),
    check("flags", "not_applicable", "no_flags"),
    check("permission", "passed", "granted", {
      facts: { permission: "authz.units.manage", permission_name: "Create, rename and move units", scope: "unit", unit: "North" },
      granted_by: [{ assignment_id: "a1", role_id: "r1", role_code: "units", role_name: "Units manager", scope_kind: "unit_with_children", unit: { id: "sales", name: "Sales" }, record_type: null, record_id: null }],
    }),
    check("record_policy", "not_applicable", "no_record"),
  ],
};

/** The action's field: its list carries the same label. */
const actionField = () => screen.getAllByLabelText("Action").find((element) => element.tagName === "INPUT")!;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("explaining access (TF-039, ADR-0039)", () => {
  it("names the first failing check of a denial, and the checks after it as not reached, in words", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["authz.access.explain", "authz.units.read"])),
      "GET /api/v1/access/actions": () => json(actions),
      "GET /api/v1/organisation-units": () => json({ data: [unit("root", "Acme", null), unit("sales", "Sales", "root"), unit("north", "North", "sales")] }),
      "GET /api/v1/access/explain?principal=p1&action=ImportRoles": () => json(denial),
    });
    renderWithProviders(<ExplainPage />, { route: "/console/explain?principal=p1&name=Grace%20Hopper" });

    expect(await screen.findByDisplayValue("Grace Hopper")).toBeInTheDocument();
    fireEvent.click(actionField());
    fireEvent.click(await screen.findByRole("option", { name: "Import roles from another tenant.", hidden: true }));
    fireEvent.click(screen.getByRole("button", { name: "Explain" }));

    expect(await screen.findByRole("heading", { name: "Grace Hopper cannot: Import roles from another tenant. The system answers 403." })).toBeInTheDocument();
    expect(screen.getByText("The first check that refuses it: The subscription.")).toBeInTheDocument();
    const checks = within(screen.getByRole("list", { name: "The checks, in order" })).getAllByRole("listitem");
    expect(checks.map((item) => item.querySelector("p")?.textContent)).toEqual([
      "The person: passed",
      "The subscription: refused",
      "Flags and kill switches: not reached",
      "The permission: not reached",
      "The record's policy: not reached",
    ]);
    expect(checks[1]).toHaveTextContent("The feature authorization.role_transfer is not part of the subscription.");
    expect(checks[3]).toHaveTextContent("Not checked: an earlier check refuses the request.");
    expect(calls.some((call) => call.key === "GET /api/v1/access/explain?principal=p1&action=ImportRoles")).toBe(true);
  });

  it("names the granting role and its scope for an allowance at a unit chosen on the tree", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(["authz.access.explain", "authz.units.read"])),
      "GET /api/v1/access/actions": () => json(actions),
      "GET /api/v1/organisation-units": () => json({ data: [unit("root", "Acme", null), unit("sales", "Sales", "root"), unit("north", "North", "sales")] }),
      "GET /api/v1/access/explain?principal=p1&action=RenameOrganisationUnit&id=north": () => json(allowance),
    });
    renderWithProviders(<ExplainPage />, { route: "/console/explain?principal=p1&name=Grace%20Hopper" });

    await screen.findByDisplayValue("Grace Hopper");
    fireEvent.click(actionField());
    fireEvent.click(await screen.findByRole("option", { name: "Rename a unit.", hidden: true }));
    const explain = screen.getByRole("button", { name: "Explain" });
    expect(explain).toBeDisabled();
    fireEvent.click(within(await screen.findByRole("tree", { name: "At the unit" })).getByText("North"));
    fireEvent.click(explain);

    expect(await screen.findByRole("heading", { name: "Grace Hopper can: Rename a unit." })).toBeInTheDocument();
    expect(screen.getByText('Grace Hopper holds the permission "Create, rename and move units" at North.')).toBeInTheDocument();
    expect(within(screen.getByRole("list", { name: "Granted by" })).getByRole("listitem")).toHaveTextContent("Units manager, at Sales and the units below it");
  });
});
