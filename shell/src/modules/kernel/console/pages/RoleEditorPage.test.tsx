import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { Route, Routes } from "react-router";
import { describe, expect, it } from "vitest";
import { json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { RoleEditorPage } from "./RoleEditorPage";

const catalogue = {
  data: [
    { name: "authz.roles.manage", module: "authz", description: "Create, change and delete tenant roles", risk: "high" },
    { name: "authz.roles.read", module: "authz", description: "List and view roles and their permissions", risk: "low" },
    { name: "identity.users.read", module: "identity", description: "List and view users and service accounts", risk: "low" },
  ],
};

const role = (extra: Record<string, unknown> = {}) => ({
  id: "r1",
  code: "user-reader",
  name: "User reader",
  description: "Reads users.",
  is_system: false,
  principal_type: "user",
  permissions: ["identity.users.read"],
  created_at: "2026-10-19T08:00:00Z",
  updated_at: "2026-10-19T08:00:00Z",
  version: 1,
  ...extra,
});

const administrator = ["authz.roles.read", "authz.roles.manage", "identity.users.read"];

const page = (route: string) =>
  renderWithProviders(
    <Routes>
      <Route path="/console/roles/new" element={<RoleEditorPage />} />
      <Route path="/console/roles/:id" element={<RoleEditorPage />} />
      <Route path="/console/roles" element={<p>The list</p>} />
    </Routes>,
    { route },
  );

describe("the role editor", () => {
  it("groups the permissions by module and shows each risk as a word", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me(administrator)), "GET /api/v1/permissions": () => json(catalogue), "GET /api/v1/roles/r1": () => json(role()) });
    page("/console/roles/r1");

    const authz = (await screen.findByText("Authorization")).closest("fieldset")!;
    expect(within(authz).getByRole("checkbox", { name: "Create, change and delete tenant roles" })).not.toBeChecked();
    expect(authz).toHaveTextContent("High risk");
    expect(authz).toHaveTextContent("authz.roles.manage");
    const identity = screen.getByText("Identity").closest("fieldset")!;
    expect(within(identity).getByRole("checkbox", { name: "List and view users and service accounts" })).toBeChecked();
    expect(identity).toHaveTextContent("Low risk");
    expect(screen.getByText("Tenant role")).toBeInTheDocument();
  });

  it("creates a role with its code, type and permissions, and shows the API's refusal at the top", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(administrator)),
      "GET /api/v1/permissions": () => json(catalogue),
      "POST /api/v1/roles": () => problem(403, { error_code: "authz.role.escalation", detail: "A role can only hold permissions you hold for the whole tenant. You do not hold: authz.roles.manage." }),
    });
    page("/console/roles/new");

    fireEvent.change(await screen.findByLabelText("Code"), { target: { value: "role-keeper" } });
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Role keeper" } });
    fireEvent.click(screen.getByRole("radio", { name: "Service accounts" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Create, change and delete tenant roles" }));
    fireEvent.click(screen.getByRole("button", { name: "Create role" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("You do not hold: authz.roles.manage.");
    const post = calls.find((call) => call.key === "POST /api/v1/roles");
    expect(JSON.parse(post?.init?.body as string)).toEqual({ code: "role-keeper", name: "Role keeper", description: "", principal_type: "service", permissions: ["authz.roles.manage"] });
  });

  it("keeps a system role read only and says why", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(administrator)),
      "GET /api/v1/permissions": () => json(catalogue),
      "GET /api/v1/roles/r1": () => json(role({ code: "read-only", name: "Read only", is_system: true, permissions: ["authz.roles.read", "identity.users.read"] })),
    });
    page("/console/roles/r1");

    expect(await screen.findByText(/System roles come with Foundation/)).toBeInTheDocument();
    expect(screen.getByText("System role")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save changes" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Delete role" })).toBeNull();
    expect(screen.getByRole("checkbox", { name: "List and view roles and their permissions" })).toBeDisabled();
  });

  it("deletes a tenant role behind a confirmation and goes back to the list", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(administrator)),
      "GET /api/v1/permissions": () => json(catalogue),
      "GET /api/v1/roles/r1": () => json(role()),
      "DELETE /api/v1/roles/r1": () => new Response(null, { status: 204 }),
    });
    page("/console/roles/r1");

    fireEvent.click(await screen.findByRole("button", { name: "Delete role" }));
    const dialog = await screen.findByRole("dialog", { name: "Delete User reader?" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete role" }));

    expect(await screen.findByText("The list")).toBeInTheDocument();
    await waitFor(() => expect(calls.some((call) => call.key === "DELETE /api/v1/roles/r1")).toBe(true));
  });
});
