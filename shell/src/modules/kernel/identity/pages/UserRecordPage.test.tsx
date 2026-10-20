import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { Route, Routes } from "react-router";
import { describe, expect, it } from "vitest";
import { json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { UserRecordPage } from "./UserRecordPage";

const user = {
  id: "u1",
  login_id: "ada@acme.localhost",
  display_name: "Ada Lovelace",
  type: "user",
  status: "active",
  email: "ada@example.com",
  home_unit: { id: "sales", name: "Sales", code: "sales" },
  last_login_at: null,
  created_at: "2026-10-01T09:00:00Z",
  updated_at: "2026-10-02T09:00:00Z",
  version: 3,
};

const list = <T,>(data: T[]) => ({
  data,
  meta: { page_number: 1, page_size: 100, total_count: data.length, total_pages: 1, has_previous: false, has_next: false, page_size_capped: false, max_page_size: 200 },
  links: { self: "", first: "", previous: null, next: null, last: "" },
});

const assignment = (id: string, scope: string, unit: { id: string; path: string[] } | null) => ({
  id,
  principal_id: "u1",
  principal_name: "Ada Lovelace",
  role_id: "r1",
  role_code: "user-reader",
  role_name: "User reader",
  scope_kind: scope,
  unit_id: unit?.id ?? null,
  record_type: null,
  record_id: null,
  unit_path: unit ? unit.path.map((name, index) => ({ id: index === unit.path.length - 1 ? unit.id : `p${index}`, name })) : [],
  created_at: "2026-10-19T09:00:00Z",
});

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

const assignments = "GET /api/v1/role-assignments?filter=principal_id:eq:u1&page_size=100";
const granter = ["identity.users.read", "authz.assignments.read", "authz.assignments.manage", "authz.roles.read", "authz.units.read"];

const page = () =>
  renderWithProviders(
    <Routes>
      <Route path="/users/:id" element={<UserRecordPage />} />
    </Routes>,
    { route: "/users/u1" },
  );

const openAccess = async () => {
  fireEvent.click(await screen.findByRole("tab", { name: "Access" }));
  return screen.findByRole("table", { name: "Roles of Ada Lovelace" });
};

describe("the user record page", () => {
  it("shows the user's details on the record template", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me(["identity.users.read"])), "GET /api/v1/users/u1": () => json(user) });
    page();

    expect(await screen.findByRole("heading", { name: "Ada Lovelace" })).toBeInTheDocument();
    expect(screen.getByText("ada@acme.localhost")).toBeInTheDocument();
    expect(screen.getByText("Active")).toBeInTheDocument();
    expect(screen.getAllByText("Sales").length).toBeGreaterThan(0);
    expect(screen.getByText("Never")).toBeInTheDocument();
  });

  it("says not found for a user outside the reader's scope, as for one that does not exist", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me(["identity.users.read"])), "GET /api/v1/users/u1": () => problem(404, { error_code: "identity.user.not_found" }) });
    page();

    expect(await screen.findByRole("heading", { name: "This user is not here" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Back to users" })).toHaveAttribute("href", "/users");
  });

  it("lists the assignments with their role, scope and unit path, and removes one behind a confirmation", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me([], granter)),
      "GET /api/v1/users/u1": () => json(user),
      [assignments]: () => json(list([assignment("a1", "unit_with_children", { id: "sales", path: ["Acme", "Sales"] }), assignment("a2", "tenant", null)])),
      "GET /api/v1/me/access?unit=sales&include_children=true": () => json({ scope: "unit_with_children:sales", permissions: granter }),
      "DELETE /api/v1/role-assignments/a1": () => new Response(null, { status: 204 }),
    });
    page();

    const table = await openAccess();
    const rows = within(table).getAllByRole("row");
    expect(rows[1]).toHaveTextContent("User reader");
    expect(rows[1]).toHaveTextContent("This unit and the units below it");
    expect(rows[1]).toHaveTextContent("Acme / Sales");
    expect(rows[2]).toHaveTextContent("The whole tenant");
    // Removing a tenant-wide grant needs the manage permission tenant-wide, which this reader holds at Sales only.
    expect(within(rows[2]!).queryByRole("button")).toBeNull();

    fireEvent.click(await within(rows[1]!).findByRole("button", { name: "Remove User reader" }));
    const dialog = await screen.findByRole("dialog", { name: "Remove User reader?" });
    expect(dialog).toHaveTextContent("Ada Lovelace loses User reader (This unit and the units below it: Acme / Sales) at once.");
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove role" }));

    await waitFor(() => expect(calls.some((call) => call.key === "DELETE /api/v1/role-assignments/a1")).toBe(true));
  });

  it("shows no Remove on a unit-and-below row to a manager of that unit alone, and Remove on a one-unit row", async () => {
    // Assignments are managed at Sales and at North alone: nothing reaches every unit below Sales (ADR-0031).
    const atOneUnit = granter.filter((permission) => permission !== "authz.assignments.manage");
    stubFetch({
      "GET /api/v1/me": () => json(me([], granter)),
      "GET /api/v1/users/u1": () => json(user),
      [assignments]: () => json(list([assignment("a1", "unit_with_children", { id: "sales", path: ["Acme", "Sales"] }), assignment("a2", "unit", { id: "north", path: ["Acme", "Sales", "North"] })])),
      "GET /api/v1/me/access?unit=sales&include_children=true": () => json({ scope: "unit_with_children:sales", permissions: atOneUnit }),
      "GET /api/v1/me/access?unit=north": () => json({ scope: "unit:north", permissions: granter }),
    });
    page();

    const table = await openAccess();
    const rows = within(table).getAllByRole("row");
    expect(rows[1]).toHaveTextContent("This unit and the units below it");
    expect(await within(rows[2]!).findByRole("button", { name: "Remove User reader" })).toBeInTheDocument();
    expect(within(rows[1]!).queryByRole("button")).toBeNull();
  });

  it("grants a role at a unit chosen on the tree, with the units below it", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me([], granter)),
      "GET /api/v1/users/u1": () => json(user),
      [assignments]: () => json(list([])),
      "GET /api/v1/roles?page_size=200&sort=name": () =>
        json(
          list([
            { id: "r1", code: "user-reader", name: "User reader", description: "", is_system: false, principal_type: "user", permissions: ["identity.users.read"], created_at: "", updated_at: "", version: 1 },
            { id: "r2", code: "service-default", name: "Service default", description: "", is_system: true, principal_type: "service", permissions: [], created_at: "", updated_at: "", version: 1 },
          ]),
        ),
      "GET /api/v1/organisation-units": () => json({ data: [unit("sales", "Sales", "acme"), unit("east", "East", "sales")] }),
      "POST /api/v1/role-assignments": () => json(assignment("a9", "unit_with_children", { id: "east", path: ["Sales", "East"] }), 201),
    });
    page();

    fireEvent.click(await screen.findByRole("tab", { name: "Access" }));
    expect(await screen.findByText("No roles here")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Grant a role" }));
    const dialog = await screen.findByRole("dialog", { name: "Grant a role to Ada Lovelace" });
    fireEvent.click(await within(dialog).findByLabelText("Role"));
    // jsdom never finishes the dropdown's transition, so its options count as hidden.
    expect(screen.queryByRole("option", { name: "Service default", hidden: true })).toBeNull();
    fireEvent.click(await screen.findByRole("option", { name: "User reader", hidden: true }));
    const tree = within(dialog).getByRole("tree", { name: "Unit" });
    fireEvent.click(within(tree).getByText("East"));
    expect(dialog).toHaveTextContent("Sales / East");
    fireEvent.click(within(dialog).getByRole("button", { name: "Grant role" }));

    await waitFor(() => expect(calls.some((call) => call.key === "POST /api/v1/role-assignments")).toBe(true));
    const post = calls.find((call) => call.key === "POST /api/v1/role-assignments");
    expect(JSON.parse(post?.init?.body as string)).toEqual({ principal_id: "u1", role_id: "r1", scope_kind: "unit_with_children", unit_id: "east" });
  });

  it("says in words, before the confirm, that a conflict rule refuses the grant, and shows the refusal naming the rule (TF-036)", async () => {
    const refusal = "The conflict rule 'Payments apart' blocks this: Ada Lovelace would hold both sides of it at East. One person should not raise and approve a payment.";
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me([], granter)),
      "GET /api/v1/users/u1": () => json(user),
      [assignments]: () => json(list([])),
      "GET /api/v1/roles?page_size=200&sort=name": () =>
        json(list([{ id: "r1", code: "payment-raiser", name: "Payment raiser", description: "", is_system: false, principal_type: "user", permissions: ["payments.raise"], created_at: "", updated_at: "", version: 1 }])),
      "GET /api/v1/organisation-units": () => json({ data: [unit("sales", "Sales", "acme"), unit("east", "East", "sales")] }),
      "POST /api/v1/role-assignments/conflicts": () =>
        json({
          data: [
            {
              rule_id: "c1",
              rule_name: "Payments apart",
              mode: "block",
              reason: "One person should not raise and approve a payment.",
              with: { assignment_id: "a1", delegation_id: null, role: "Payment approver", scope_kind: "unit_with_children", unit_id: "sales", unit_name: "Sales", source: "assignment" },
              unit_id: "east",
              unit_name: "East",
            },
          ],
        }),
      "POST /api/v1/role-assignments": () => problem(409, { title: "Conflict", detail: refusal, error_code: "authz.sod.conflict" }),
    });
    page();

    fireEvent.click(await screen.findByRole("tab", { name: "Access" }));
    fireEvent.click(await screen.findByRole("button", { name: "Grant a role" }));
    const dialog = await screen.findByRole("dialog", { name: "Grant a role to Ada Lovelace" });
    fireEvent.click(await within(dialog).findByLabelText("Role"));
    fireEvent.click(await screen.findByRole("option", { name: "Payment raiser", hidden: true }));
    fireEvent.click(within(within(dialog).getByRole("tree", { name: "Unit" })).getByText("East"));

    expect(await within(dialog).findByRole("status")).toHaveTextContent("The conflict rules refuse this grant");
    expect(within(dialog).getByText("Payments apart: Ada Lovelace already holds Payment approver at Sales and the units below it, so would hold both sides at East.")).toBeInTheDocument();
    // The dialog asks again as the grant changes: first at the home unit, last at the unit chosen.
    const asked = calls.findLast((call) => call.key === "POST /api/v1/role-assignments/conflicts");
    expect(JSON.parse(asked?.init?.body as string)).toEqual({ principal_id: "u1", role_id: "r1", scope_kind: "unit_with_children", unit_id: "east" });

    fireEvent.click(within(dialog).getByRole("button", { name: "Grant role" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent(refusal);
  });

  it("hides the access for a reader without the permission to read assignments", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me(["identity.users.read"])), "GET /api/v1/users/u1": () => json(user) });
    page();

    fireEvent.click(await screen.findByRole("tab", { name: "Access" }));

    expect(await screen.findByText("Access is hidden")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Grant a role" })).toBeNull();
  });
});
