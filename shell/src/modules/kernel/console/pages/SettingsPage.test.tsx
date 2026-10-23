import { afterEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { SettingsPage } from "./SettingsPage";

const administrator = ["authz.units.read", "settings.values.manage", "settings.values.read"];
const reader = ["settings.values.read"];
const root = {
  id: "01a15100-0000-7000-8000-0000000000c1",
  name: "Acme",
  code: "acme",
  kind: "root",
  parent_id: null,
  depth: 0,
  status: "active",
  created_at: "2026-10-19T09:00:00Z",
  updated_at: "2026-10-19T09:00:00Z",
  version: 1,
};
const operations = {
  ...root,
  id: "01a15100-0000-7000-8000-0000000000c2",
  name: "Operations",
  code: "operations",
  kind: "unit",
  parent_id: root.id,
  depth: 1,
};
const warehouse = {
  ...root,
  id: "01a15100-0000-7000-8000-0000000000c3",
  name: "Warehouse",
  code: "warehouse",
  kind: "unit",
  parent_id: operations.id,
  depth: 2,
};
const ravi = { id: "01a15100-0000-7000-8000-0000000000d1", name: "Ravi" };
const notSet = { level: "default", unit: null, changed_by: null, changed_at: null };

const view = (key: string, module: string, over: Record<string, unknown> = {}) => ({
  key,
  module,
  type: "number",
  description: `What ${key} does.`,
  requirement: "A whole number from 15 to 480.",
  choices: null,
  minimum: 15,
  maximum: 480,
  whole_number: true,
  scopes: ["tenant"],
  default: 60,
  value: 60,
  source: notSet,
  set_here: false,
  ...over,
});

const tenantList = {
  scope: "tenant",
  unit: null,
  data: [
    view("identity.invitation.valid_days", "identity", {
      requirement: "A whole number from 1 to 30.",
      minimum: 1,
      maximum: 30,
      scopes: ["tenant", "unit"],
      default: 3,
      value: 3,
    }),
    view("identity.session.idle_minutes", "identity", {
      value: 30,
      set_here: true,
      source: { level: "tenant", unit: null, changed_by: ravi, changed_at: "2026-10-19T10:00:00Z" },
    }),
  ],
};

const warehouseList = {
  scope: "unit",
  unit: { id: warehouse.id, name: "Warehouse", path: "Acme / Operations / Warehouse" },
  data: [
    view("identity.invitation.valid_days", "identity", {
      requirement: "A whole number from 1 to 30.",
      minimum: 1,
      maximum: 30,
      scopes: ["tenant", "unit"],
      default: 3,
      value: 14,
      source: {
        level: "unit",
        unit: { id: operations.id, name: "Operations", path: "Acme / Operations" },
        changed_by: ravi,
        changed_at: "2026-10-19T10:00:00Z",
      },
    }),
  ],
};

function open(
  permissions = administrator,
  handlers: Record<string, (init: RequestInit | undefined) => Response | Promise<Response>> = {},
) {
  const stub = stubFetch({
    "GET /api/v1/me": () => json(me(permissions)),
    "GET /api/v1/settings?scope=tenant": () => json(tenantList),
    [`GET /api/v1/settings?scope=unit&unit=${warehouse.id}`]: () => json(warehouseList),
    "GET /api/v1/organisation-units": () => json({ data: [root, operations, warehouse] }),
    [`GET /api/v1/me/access?unit=${warehouse.id}`]: () => json({ scope: "unit", permissions }),
    ...handlers,
  });
  renderWithProviders(<SettingsPage />, { route: "/console/settings" });
  return stub;
}

const row = async (name: string) => within(await screen.findByRole("article", { name }));

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the console's settings page (TF-058, ADR-0036)", () => {
  it("shows the settings by module, each with its value here, its default and where the value comes from [ADR-0036]", async () => {
    open();

    expect(await screen.findByRole("heading", { level: 3, name: "Sign-in and invitations" })).toBeInTheDocument();
    const idle = await row("A session ends without activity after");
    expect(idle.getByText("identity.session.idle_minutes")).toBeInTheDocument();
    expect(idle.getByText("30 minutes")).toBeInTheDocument();
    expect(idle.getByText("Default: 60 minutes")).toBeInTheDocument();
    expect(idle.getByText(/^Set here by Ravi on /)).toBeInTheDocument();
    const invitations = await row("An invitation link stays valid for");
    expect(invitations.getByText("The default: nobody has set a value.")).toBeInTheDocument();
    // Only a value set here can be reset.
    expect(invitations.queryByRole("button", { name: /^Reset/ })).not.toBeInTheDocument();
    expect(idle.getByRole("button", { name: "Reset: A session ends without activity after" })).toBeInTheDocument();
  });

  it("shows a unit's values with the unit above they are inherited from, when a unit is picked on the tree [ADR-0036]", async () => {
    const user = userEvent.setup();
    const { calls } = open();
    await screen.findByRole("article", { name: "An invitation link stays valid for" });

    await user.click(screen.getByRole("radio", { name: "One unit" }));
    expect(await screen.findByText("Choose a unit on the tree to see its values.")).toBeInTheDocument();
    // A tree item is named by all it holds, its children included, so the item is found by its own label.
    const tree = await screen.findByRole("tree");
    await user.click(within(tree).getByText("Warehouse").closest<HTMLElement>("[role=treeitem]")!);

    const invitations = await row("An invitation link stays valid for");
    expect(invitations.getByText("14 days")).toBeInTheDocument();
    expect(invitations.getByText(/^From Acme \/ Operations, set by Ravi on /)).toBeInTheDocument();
    expect(calls.some((call) => call.key === `GET /api/v1/settings?scope=unit&unit=${warehouse.id}`)).toBe(true);
  });

  it("changes a value in a form dialog and shows the server's rule on the field when it refuses [TF-057 AC2, TF-171 AC3]", async () => {
    const user = userEvent.setup();
    const { calls } = open(administrator, {
      "PUT /api/v1/settings/identity.session.idle_minutes": () =>
        problem(400, {
          title: "Bad Request",
          error_code: "settings.value.invalid",
          detail: "The value for 'identity.session.idle_minutes' is refused. A whole number from 15 to 480.",
          errors: [{ field: "value", code: "invalid", message: "A whole number from 15 to 480." }],
        }),
    });
    const idle = await row("A session ends without activity after");

    await user.click(idle.getByRole("button", { name: "Change: A session ends without activity after" }));
    const dialog = await screen.findByRole("dialog", { name: "Change: A session ends without activity after" });
    expect(within(dialog).getByText("For The whole organisation.")).toBeInTheDocument();
    const value = within(dialog).getByRole("textbox", { name: "Value" });
    await user.clear(value);
    await user.type(value, "5");
    await user.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(
        within(dialog).getByText("A whole number from 15 to 480.", { selector: ".mantine-InputWrapper-error" }),
      ).toBeInTheDocument(),
    );
    const sent = calls.find((call) => call.key === "PUT /api/v1/settings/identity.session.idle_minutes");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({ scope: "tenant", unit_id: null, value: 5 });
  });

  it("resets a value behind a confirmation and shows the inherited value afterwards [TF-058 AC2]", async () => {
    const user = userEvent.setup();
    let list = tenantList;
    const { calls } = open(administrator, {
      "GET /api/v1/settings?scope=tenant": () => json(list),
      "DELETE /api/v1/settings/identity.session.idle_minutes?scope=tenant": () => {
        list = { ...tenantList, data: [tenantList.data[0]!, view("identity.session.idle_minutes", "identity")] };
        return json(view("identity.session.idle_minutes", "identity"));
      },
    });
    const idle = await row("A session ends without activity after");

    await user.click(idle.getByRole("button", { name: "Reset: A session ends without activity after" }));
    // The confirmation opens over the page's own grid; under the whole suite's load it takes longer than the default second.
    const confirm = await screen.findByRole("dialog", { name: "Reset this setting?" }, { timeout: 5_000 });
    expect(confirm).toHaveTextContent("The value set here is removed, and the value from above applies instead");
    expect(calls.some((call) => call.key.startsWith("DELETE"))).toBe(false);
    await user.click(within(confirm).getByRole("button", { name: "Reset" }));

    await waitFor(() =>
      expect(
        calls.some((call) => call.key === "DELETE /api/v1/settings/identity.session.idle_minutes?scope=tenant"),
      ).toBe(true),
    );
    const after = await row("A session ends without activity after");
    await waitFor(() => expect(after.getByText("The default: nobody has set a value.")).toBeInTheDocument());
    expect(after.getByText("60 minutes")).toBeInTheDocument();
  });

  it("shows the history of a setting in words, newest first [ADR-0036]", async () => {
    const user = userEvent.setup();
    open(administrator, {
      "GET /api/v1/settings/identity.session.idle_minutes/history?scope=tenant&page_size=50": () =>
        json({
          data: [
            {
              id: "h2",
              kind: "set",
              before: 45,
              after: 30,
              changed_by_id: ravi.id,
              changed_by_name: "Ravi",
              changed_at: "2026-10-19T10:00:00Z",
            },
            {
              id: "h1",
              kind: "set",
              before: null,
              after: 45,
              changed_by_id: ravi.id,
              changed_by_name: "Ravi",
              changed_at: "2026-10-18T10:00:00Z",
            },
          ],
          meta: {
            page_number: 1,
            page_size: 50,
            total_count: 2,
            total_pages: 1,
            has_previous: false,
            has_next: false,
            page_size_capped: false,
            max_page_size: 200,
          },
          links: { self: "/x", first: "/x", previous: null, next: null, last: "/x" },
        }),
    });
    const idle = await row("A session ends without activity after");

    await user.click(idle.getByRole("button", { name: "History: A session ends without activity after" }));
    const history = await screen.findByRole("dialog", { name: "History: A session ends without activity after" });
    expect(await within(history).findByText("Changed from 45 minutes to 30 minutes")).toBeInTheDocument();
    expect(within(history).getByText("Set to 45 minutes")).toBeInTheDocument();
  });

  it("offers no change and no reset to a reader without settings.values.manage [ADR-0036]", async () => {
    open(reader);
    const idle = await row("A session ends without activity after");

    expect(idle.queryByRole("button", { name: /^Change/ })).not.toBeInTheDocument();
    expect(idle.queryByRole("button", { name: /^Reset/ })).not.toBeInTheDocument();
    expect(idle.getByRole("button", { name: /^History/ })).toBeInTheDocument();
  });

  it("chooses the MFA policy's roles from the tenant's roles and saves their codes [TF-013 AC1, ADR-0047]", async () => {
    const user = userEvent.setup();
    const roles = view("identity.mfa.roles", "identity", {
      type: "json",
      requirement: "A list of role codes.",
      minimum: null,
      maximum: null,
      whole_number: false,
      default: [],
      value: ["buyer"],
    });
    const role = (code: string, name: string) => ({
      id: `01a15100-0000-7000-8000-0000000${code.length}00e1`,
      code,
      name,
      description: "",
      is_system: false,
      principal_type: "user",
      permissions: [],
      created_at: "2026-10-19T09:00:00Z",
      updated_at: "2026-10-19T09:00:00Z",
      version: 1,
    });
    const { calls } = open(administrator, {
      "GET /api/v1/settings?scope=tenant": () => json({ ...tenantList, data: [roles] }),
      "GET /api/v1/roles?page_size=200&sort=name": () =>
        json({
          data: [role("buyer", "Buyer"), role("tenant-administrator", "Tenant administrator")],
          meta: {
            page_number: 1,
            page_size: 200,
            total_count: 2,
            total_pages: 1,
            has_previous: false,
            has_next: false,
            page_size_capped: false,
            max_page_size: 200,
          },
          links: { self: "", first: "", previous: null, next: null, last: "" },
        }),
      "PUT /api/v1/settings/identity.mfa.roles": () => json(roles),
    });
    const setting = await row("The roles whose holders need a second factor");

    await user.click(setting.getByRole("button", { name: "Change: The roles whose holders need a second factor" }));
    const dialog = await screen.findByRole("dialog", { name: "Change: The roles whose holders need a second factor" });
    const picker = within(dialog).getByLabelText(/^Roles/);
    await user.click(picker);
    // The dropdown is in a transition jsdom never finishes, so its options count as hidden; the click still chooses one.
    fireEvent.click(await screen.findByRole("option", { name: "Tenant administrator", hidden: true }));
    await user.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(calls.some((call) => call.key === "PUT /api/v1/settings/identity.mfa.roles")).toBe(true),
    );
    const sent = calls.find((call) => call.key === "PUT /api/v1/settings/identity.mfa.roles");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({
      scope: "tenant",
      unit_id: null,
      value: ["buyer", "tenant-administrator"],
    });
  });
  it("sets the signing policy per document type, an always-signed type fixed, and saves it as one value [TF-063 AC1, ADR-0051]", async () => {
    const user = userEvent.setup();
    const policies = view("compliance.signing.policies", "compliance", {
      type: "json",
      requirement: "An object keyed by document type.",
      minimum: null,
      maximum: null,
      whole_number: false,
      default: {},
      value: {},
    });
    const policy = (required: boolean, meanings: string[]) => ({ required, meanings, reauthenticate: true });
    const { calls } = open(administrator, {
      "GET /api/v1/settings?scope=tenant": () => json({ ...tenantList, data: [policies] }),
      "GET /api/v1/signatures/types": () =>
        json({
          data: [
            {
              record_type: "access_review_decision",
              name: "Access review decision",
              meanings: ["approved", "rejected"],
              always_signed: false,
              policy: policy(false, ["approved", "rejected"]),
              default: policy(false, ["approved", "rejected"]),
            },
            {
              record_type: "audit_trail_review",
              name: "Audit-trail review",
              meanings: ["reviewed"],
              always_signed: true,
              policy: policy(true, ["reviewed"]),
              default: policy(true, ["reviewed"]),
            },
          ],
        }),
      "PUT /api/v1/settings/compliance.signing.policies": () => json(policies),
    });
    const setting = await row("Signing policy");
    expect(setting.getByText("Every kind of document as its module sets it.")).toBeInTheDocument();

    await user.click(setting.getByRole("button", { name: "Change: Signing policy" }));
    const dialog = await screen.findByRole("dialog", { name: "Change: Signing policy" });
    const decisions = within(await within(dialog).findByRole("group", { name: "Access review decision" }));
    const reviews = within(within(dialog).getByRole("group", { name: "Audit-trail review" }));
    expect(reviews.getByRole("switch", { name: "A signature is required" })).toBeDisabled();
    expect(reviews.getByRole("switch", { name: "A signature is required" })).toBeChecked();
    await user.click(decisions.getByRole("switch", { name: "A signature is required" }));
    await user.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(calls.some((call) => call.key === "PUT /api/v1/settings/compliance.signing.policies")).toBe(true),
    );
    const sent = calls.find((call) => call.key === "PUT /api/v1/settings/compliance.signing.policies");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({
      scope: "tenant",
      unit_id: null,
      value: { access_review_decision: { required: true, meanings: ["approved", "rejected"], reauthenticate: true } },
    });
  });
});
