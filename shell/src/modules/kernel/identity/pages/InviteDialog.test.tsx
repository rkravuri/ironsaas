import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes, useParams } from "react-router";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { UsersPage } from "./UsersPage";

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
const tree = { data: [unit("root", "Acme", null), unit("branch", "Branch", "root"), unit("finance", "Finance", "root")] };
const emptyList = {
  data: [],
  meta: { page_number: 1, page_size: 50, total_count: 0, total_pages: 1, has_previous: false, has_next: false, page_size_capped: false, max_page_size: 200 },
  links: { self: "", first: "", previous: null, next: null, last: "" },
};
const manager = ["authz.units.read", "identity.users.manage", "identity.users.read"];

function Record() {
  const { id } = useParams();
  return <p>the record of {id}</p>;
}

/** The Users page with the API from memory: the person manages users at Branch and nowhere else. */
function openUsers(route: string, extra: Record<string, () => Response> = {}) {
  const fetch = stubFetch({
    "GET /api/v1/me": () => json({ ...me(["identity.users.read"], manager), home_unit: { id: "branch", name: "Branch", code: "branch" } }),
    "GET /api/v1/organisation-units": () => json(tree),
    "GET /api/v1/me/access?unit=branch": () => json({ scope: "unit:branch", permissions: manager }),
    "GET /api/v1/me/access?unit=finance": () => json({ scope: "unit:finance", permissions: ["identity.users.read"] }),
    "GET /api/v1/me/access?unit=root": () => json({ scope: "unit:root", permissions: [] }),
    ...extra,
  });
  // The grid asks for its rows with a query string; any of them answers an empty page.
  const listing = fetch.fetchMock.getMockImplementation();
  fetch.fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) =>
    typeof input === "string" && input.startsWith("/api/v1/users?") ? json(emptyList) : listing!(input, init),
  );
  renderWithProviders(
    <Routes>
      <Route path="/users" element={<UsersPage />} />
      <Route path="/users/:id" element={<Record />} />
    </Routes>,
    { route },
  );
  return fetch;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("inviting a person", () => {
  it("creates the user in the unit picked on the tree, shows the link once with a copy button, then opens the record", async () => {
    const { calls } = openUsers("/users", {
      "POST /api/v1/users": () =>
        json(
          {
            user: { id: "new-1", login_id: "grace@acme.localhost", display_name: "Grace Hopper", type: "user", status: "invited", email: null, home_unit_id: "branch", last_login_at: null, created_at: "2026-10-19T10:00:00Z", updated_at: "2026-10-19T10:00:00Z", version: 1 },
            invitation_link: "/invitations/secret-token",
            invitation_expires_at: "2026-10-22T10:00:00Z",
          },
          201,
        ),
    });
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Invite a person" }));
    const dialog = await screen.findByRole("dialog", { name: "Invite a person" });

    await fill(user, within(dialog).getByLabelText("Display name"), "Grace Hopper");
    await fill(user, within(dialog).getByLabelText(/^Login identifier/), "grace@acme.localhost");
    // The home unit is chosen already, and the path of the choice is written under the tree.
    expect(await within(dialog).findByText("Acme / Branch")).toBeInTheDocument();
    await user.click(within(dialog).getByRole("button", { name: "Invite" }));

    const done = await screen.findByRole("dialog", { name: "Grace Hopper is invited" });
    expect(within(done).getByLabelText("Invitation link")).toHaveValue(`${window.location.origin}/invitations/secret-token`);
    expect(within(done).getByRole("button", { name: "Copy the link" })).toBeInTheDocument();
    expect(done).toHaveTextContent(/Email delivery comes later: send this link to them yourself/);
    const sent = calls.find((call) => call.key === "POST /api/v1/users");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({ display_name: "Grace Hopper", login_id: "grace@acme.localhost", home_unit_id: "branch" });

    await user.click(within(done).getByRole("button", { name: "Open their record" }));
    expect(await screen.findByText("the record of new-1")).toBeInTheDocument();
    expect(screen.queryByText(/secret-token/)).not.toBeInTheDocument();
  });

  it("puts the API's field problems on their fields", async () => {
    openUsers("/users", {
      "POST /api/v1/users": () =>
        problem(409, {
          title: "Conflict",
          detail: "Someone already signs in as 'ada@acme.localhost'.",
          error_code: "identity.user.login_id_taken",
          errors: [{ field: "login_id", code: "taken", message: "Another account has this login id. Choose another." }],
        }),
    });
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Invite a person" }));
    const dialog = await screen.findByRole("dialog", { name: "Invite a person" });
    await fill(user, within(dialog).getByLabelText("Display name"), "Ada Lovelace");
    await fill(user, within(dialog).getByLabelText(/^Login identifier/), "ada@acme.localhost");
    await within(dialog).findByText("Acme / Branch");
    await user.click(within(dialog).getByRole("button", { name: "Invite" }));

    await waitFor(() => expect(within(dialog).getByLabelText(/^Login identifier/)).toHaveAccessibleDescription(/Another account has this login id/));
  });

  it("refuses a unit where /me/access does not allow managing users, before asking the API", async () => {
    const { calls } = openUsers("/users");
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Invite a person" }));
    const dialog = await screen.findByRole("dialog", { name: "Invite a person" });
    await fill(user, within(dialog).getByLabelText("Display name"), "Ada Lovelace");
    await fill(user, within(dialog).getByLabelText(/^Login identifier/), "ada@acme.localhost");
    await user.click(await within(dialog).findByText("Finance"));

    expect(await within(dialog).findByText(/You cannot invite people into this unit/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole("button", { name: "Invite" }));
    expect(calls.some((call) => call.key === "POST /api/v1/users")).toBe(false);
  });

  it("opens from the quick action's address", async () => {
    openUsers("/users?invite=1");
    expect(await screen.findByRole("dialog", { name: "Invite a person" })).toBeInTheDocument();
  });

  it("offers nothing to a reader who manages nobody", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me(["identity.users.read"])) });
    renderWithProviders(<UsersPage />, { route: "/?invite=1" });
    await screen.findByRole("heading", { name: "Users" });
    expect(screen.queryByRole("button", { name: "Invite a person" })).not.toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });
});

describe("the Users page and the invite dialog follow the unit you work for (ADR-0038)", () => {
  const working = (unitId: string, name: string, allUnits = false) => () =>
    json({
      ...me(["identity.users.read"], manager),
      home_unit: { id: "branch", name: "Branch", code: "branch" },
      working_unit: { id: unitId, name, code: unitId },
      all_units: allUnits,
      working_units: {
        units: [
          { id: "branch", name: "Branch", code: "branch", depth: 1, path: [{ id: "root", name: "Acme" }, { id: "branch", name: "Branch" }] },
          { id: "finance", name: "Finance", code: "finance", depth: 1, path: [{ id: "root", name: "Acme" }, { id: "finance", name: "Finance" }] },
        ],
        all_units_available: true,
      },
    });

  it("says the list keeps to the unit, and a person invited joins it unless another is chosen", async () => {
    openUsers("/users", { "GET /api/v1/me": working("finance", "Finance") });
    const user = userEvent.setup();

    expect(await screen.findByText(/The list keeps to Finance and the units below it\./)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Invite a person" }));
    const dialog = await screen.findByRole("dialog", { name: "Invite a person" });
    expect(await within(dialog).findByText("Acme / Finance")).toBeInTheDocument();
  });

  it("says the list shows all your units, and a person invited joins your home unit", async () => {
    openUsers("/users", { "GET /api/v1/me": working("branch", "Branch", true) });
    const user = userEvent.setup();

    expect(await screen.findByText(/The list shows all your units\./)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Invite a person" }));
    const dialog = await screen.findByRole("dialog", { name: "Invite a person" });
    expect(await within(dialog).findByText("Acme / Branch")).toBeInTheDocument();
  });
});
