import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { UnitsPage } from "./UnitsPage";

const unit = (id: string, name: string, parent: string | null, extra: Record<string, unknown> = {}) => ({
  id,
  name,
  code: name.toLowerCase(),
  kind: parent === null ? "root" : "unit",
  parent_id: parent,
  depth: parent === null ? 0 : 1,
  status: "active",
  created_at: "2026-10-19T08:00:00Z",
  updated_at: "2026-10-19T08:00:00Z",
  version: 1,
  ...extra,
});

const tree = {
  data: [unit("root", "Acme", null), unit("finance", "Finance", "root"), unit("sales", "Sales", "root"), unit("closed", "Closed", "root", { status: "inactive" })],
};

const kinds = {
  root_kind: "root",
  data: [
    { code: "root", name: "Root", child_kinds: ["unit"] },
    { code: "unit", name: "Unit", child_kinds: ["unit"] },
  ],
};

/** /me/access for every unit of the tree: the same permissions at each, unless a unit has its own. */
const access = (permissions: string[], perUnit: Record<string, string[]> = {}) =>
  Object.fromEntries(tree.data.map((unit) => [`GET /api/v1/me/access?unit=${unit.id}`, () => json({ scope: `unit:${unit.id}`, permissions: perUnit[unit.id] ?? permissions })]));

const stub = (permissions: string[], extra = {}, perUnit: Record<string, string[]> = {}) =>
  stubFetch({
    "GET /api/v1/me": () => json(me(permissions)),
    "GET /api/v1/organisation-units": () => json(tree),
    "GET /api/v1/organisation-kinds": () => json(kinds),
    ...access(permissions, perUnit),
    ...extra,
  });

const record = () => screen.getByText("Path").closest("table")!.parentElement!;

/** A tree item by its own label: an item's accessible name also holds the names of the items below it. */
const item = (label: string) => within(screen.getByRole("tree")).getByText(label).closest<HTMLElement>("[role=treeitem]")!;

describe("the quick action Add a unit", () => {
  it("chooses the person's home unit and opens its dialog for a new unit there", async () => {
    stubFetch({
      "GET /api/v1/me": () => json({ ...me(["authz.units.read", "authz.units.manage"]), home_unit: { id: "finance", name: "Finance", code: "finance" } }),
      "GET /api/v1/organisation-units": () => json(tree),
      "GET /api/v1/organisation-kinds": () => json(kinds),
      ...access(["authz.units.read", "authz.units.manage"]),
    });
    renderWithProviders(<UnitsPage />, { route: "/console/units?add=1" });

    expect(await screen.findByRole("dialog", { name: "A new unit under Finance" })).toBeInTheDocument();
  });
});

describe("the organisation units page", () => {
  it("shows the tree with the root chosen, and an inactive unit says so in words", async () => {
    stub(["authz.units.read", "authz.units.manage"]);
    renderWithProviders(<UnitsPage />);

    const treeElement = await screen.findByRole("tree", { name: "Organisation units" });
    expect(within(treeElement).getAllByRole("treeitem")).toHaveLength(4);
    expect(item("Closed")).toHaveTextContent("Inactive");
    await waitFor(() => expect(record()).toHaveTextContent("Acme"));
    expect(record()).toHaveTextContent("Active");
    expect(record()).toHaveTextContent("The root stands for the whole tenant");
    expect(screen.getByRole("button", { name: "Add a unit here" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Move" })).toBeNull();
  });

  it("follows the keyboard: the record shows the unit the arrow keys reach", async () => {
    stub(["authz.units.read"]);
    renderWithProviders(<UnitsPage />);

    await screen.findByRole("tree");
    const root = item("Acme");
    root.focus();
    fireEvent.keyDown(root, { code: "ArrowDown", key: "ArrowDown" });

    await waitFor(() => expect(record()).toHaveTextContent("Finance"));
    expect(item("Finance")).toHaveAttribute("aria-selected", "true");
  });

  it("creates a unit under the chosen one and puts a taken code on its field", async () => {
    const { calls } = stub(["authz.units.read", "authz.units.manage"], {
      "POST /api/v1/organisation-units/root/children": () =>
        problem(409, { error_code: "authz.unit.code_taken", errors: [{ field: "code", code: "taken", message: "Another unit has this code. Choose another." }] }),
    });
    renderWithProviders(<UnitsPage />);

    fireEvent.click(await screen.findByRole("button", { name: "Add a unit here" }));
    const dialog = await screen.findByRole("dialog", { name: "A new unit under Acme" });
    fireEvent.change(within(dialog).getByLabelText("Name"), { target: { value: "Legal" } });
    fireEvent.change(within(dialog).getByLabelText("Code"), { target: { value: "finance" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create the unit" }));

    expect(await within(dialog).findByText("Another unit has this code. Choose another.")).toBeInTheDocument();
    const post = calls.find((call) => call.key === "POST /api/v1/organisation-units/root/children");
    expect(JSON.parse(post?.init?.body as string)).toEqual({ name: "Legal", code: "finance", kind: "unit" });
  });

  it("offers the actions where the principal may manage the chosen unit, and not elsewhere", async () => {
    // Held anywhere, so navigation shows the page; at the root only reading, at Sales managing too (ADR-0031).
    stubFetch({
      "GET /api/v1/me": () => json(me([], ["authz.units.read", "authz.units.manage"])),
      "GET /api/v1/organisation-units": () => json(tree),
      "GET /api/v1/organisation-kinds": () => json(kinds),
      ...access(["authz.units.read"], { sales: ["authz.units.read", "authz.units.manage"] }),
    });
    renderWithProviders(<UnitsPage />);

    await screen.findByRole("tree");
    await waitFor(() => expect(record()).toHaveTextContent("Acme"));
    expect(screen.queryByRole("button", { name: "Rename" })).toBeNull();
    fireEvent.click(within(item("Sales")).getByText("Sales"));

    expect(await screen.findByRole("button", { name: "Rename" })).toBeInTheDocument();
  });

  it("is read only without the manage permission", async () => {
    stub(["authz.units.read"]);
    renderWithProviders(<UnitsPage />);

    await screen.findByRole("tree");
    await waitFor(() => expect(record()).toHaveTextContent("Acme"));
    expect(screen.queryByRole("button", { name: "Add a unit here" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Rename" })).toBeNull();
  });
});

describe("a unit's life (TF-223, ADR-0038)", () => {
  const manage = ["authz.units.read", "authz.units.manage"];
  const preview = {
    from: unit("sales", "Sales", "root"),
    into: unit("finance", "Finance", "root"),
    parts: [
      { code: "records", module: "orders", record_type: "sales_order", record_type_name: "Sales orders", count: 3, items: [], conflicts: [] },
      { code: "people", module: "identity", record_type: null, record_type_name: null, count: 22, items: ["Ada Lovelace", "Grace Hopper"], conflicts: [] },
      { code: "assignments", module: "authorization", record_type: null, record_type_name: null, count: 1, items: ["Ada Lovelace (Reader)"], conflicts: ["Grace Hopper (Reader)"] },
      { code: "settings", module: "settings", record_type: null, record_type_name: null, count: 0, items: [], conflicts: ["numbering.prefix"] },
      { code: "shares", module: "data", record_type: null, record_type_name: null, count: 0, items: [], conflicts: [] },
      { code: "children", module: "authorization", record_type: null, record_type_name: null, count: 0, items: [], conflicts: [] },
    ],
  };

  it("reactivates a deactivated unit", async () => {
    const { calls } = stub(manage, { "POST /api/v1/organisation-units/closed/reactivate": () => json(unit("closed", "Closed", "root")) });
    renderWithProviders(<UnitsPage />);

    await screen.findByRole("tree");
    fireEvent.click(within(item("Closed")).getByText("Closed"));
    fireEvent.click(await screen.findByRole("button", { name: "Reactivate" }));

    await waitFor(() => expect(calls.some((call) => call.key === "POST /api/v1/organisation-units/closed/reactivate")).toBe(true));
  });

  it("shows what the modules need of the unit, item by item in words, and whether it is ready", async () => {
    stub(manage, {
      "GET /api/v1/organisation-units/root/readiness": () =>
        json({
          unit_id: "root",
          ready: false,
          items: [
            { module: "orders", code: "orders.numbering", label: "A numbering series for orders", done: true },
            { module: "ledger", code: "ledger.calendar", label: "A fiscal calendar", done: false },
          ],
        }),
    });
    renderWithProviders(<UnitsPage />);

    expect(await screen.findByText("1 thing to do")).toBeInTheDocument();
    expect(screen.getByText("Done: A numbering series for orders")).toBeInTheDocument();
    expect(screen.getByText("To do: A fiscal calendar")).toBeInTheDocument();
  });

  it("says a unit is ready when no module asks anything of it", async () => {
    stub(manage, { "GET /api/v1/organisation-units/root/readiness": () => json({ unit_id: "root", ready: true, items: [] }) });
    renderWithProviders(<UnitsPage />);

    expect(await screen.findByText("Ready")).toBeInTheDocument();
    expect(screen.getByText("No module asks anything of a new unit yet.")).toBeInTheDocument();
  });

  it("previews a merge by kind, in words with what stays behind, and merges only when asked", async () => {
    const { calls } = stub(manage, {
      "GET /api/v1/organisation-units/sales/merge-preview?into=finance": () => json(preview),
      "POST /api/v1/organisation-units/sales/merge": () => json({ ...preview, from: unit("sales", "Sales", "root", { status: "inactive", merged_into_id: "finance" }) }),
    });
    renderWithProviders(<UnitsPage />);

    await screen.findByRole("tree");
    fireEvent.click(within(item("Sales")).getByText("Sales"));
    fireEvent.click(await screen.findByRole("button", { name: "Merge into another unit" }));
    const dialog = await screen.findByRole("dialog", { name: "Merge Sales into another unit" });
    fireEvent.click(within(dialog).getByLabelText("Merge into"));
    // Only units outside Sales may stay; the root never merges, but may be merged into.
    fireEvent.click(await screen.findByRole("option", { name: "Acme / Finance", hidden: true }));

    const shown = await within(dialog).findByRole("table");
    expect(within(dialog).getByText("What moves from Sales to Finance")).toBeInTheDocument();
    expect(shown).toHaveTextContent("Sales orders: 3 records, from orders");
    expect(shown).not.toHaveTextContent("sales_order");
    expect(shown).toHaveTextContent("22 people whose home unit becomes Finance: Ada Lovelace, Grace Hopper, and 20 more");
    expect(shown).toHaveTextContent("1 is held at Finance already, so it is removed: Grace Hopper (Reader)");
    expect(shown).toHaveTextContent("1 is not moved, because Finance has its own value: numbering.prefix");
    expect(within(within(shown).getByRole("rowheader", { name: "Units below" }).closest("tr")!).getByText("Nothing")).toBeInTheDocument();
    expect(calls.some((call) => call.key === "POST /api/v1/organisation-units/sales/merge")).toBe(false);

    fireEvent.click(within(dialog).getByRole("button", { name: "Merge Sales into Finance" }));

    await waitFor(() => expect(calls.some((call) => call.key === "POST /api/v1/organisation-units/sales/merge")).toBe(true));
    const post = calls.find((call) => call.key === "POST /api/v1/organisation-units/sales/merge");
    expect(JSON.parse(post?.init?.body as string)).toEqual({ into_id: "finance" });
  });

  it("says why a merge is not possible, with the correlation id, and merges nothing", async () => {
    const { calls } = stub(manage, {
      "GET /api/v1/organisation-units/sales/merge-preview?into=finance": () =>
        problem(400, { error_code: "authz.unit.kind_not_allowed", detail: "A Unit cannot hold a Team. It can hold: none." }, "corr-merge"),
    });
    renderWithProviders(<UnitsPage />);

    await screen.findByRole("tree");
    fireEvent.click(within(item("Sales")).getByText("Sales"));
    fireEvent.click(await screen.findByRole("button", { name: "Merge into another unit" }));
    const dialog = await screen.findByRole("dialog", { name: "Merge Sales into another unit" });
    fireEvent.click(within(dialog).getByLabelText("Merge into"));
    fireEvent.click(await screen.findByRole("option", { name: "Acme / Finance", hidden: true }));

    const refusal = await within(dialog).findByRole("alert");
    expect(refusal).toHaveTextContent("This merge is not possible");
    expect(refusal).toHaveTextContent("corr-merge");
    fireEvent.click(within(dialog).getByRole("button", { name: "Merge Sales into Finance" }));
    expect(await within(dialog).findByText("This merge is not possible. Choose another unit, or cancel.")).toBeInTheDocument();
    expect(calls.some((call) => call.key === "POST /api/v1/organisation-units/sales/merge")).toBe(false);
  });

  it("says a merged unit was merged and where, and offers no way back", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(manage)),
      "GET /api/v1/organisation-units": () => json({ data: [...tree.data.filter((u) => u.id !== "sales"), unit("sales", "Sales", "root", { status: "inactive", merged_into_id: "finance" })] }),
      "GET /api/v1/organisation-kinds": () => json(kinds),
      ...access(manage),
    });
    renderWithProviders(<UnitsPage />);

    await screen.findByRole("tree");
    fireEvent.click(within(item("Sales")).getByText("Sales"));

    expect(await screen.findByText("This unit was merged into Finance. What it held is there now.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Reactivate" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Merge into another unit" })).toBeNull();
  });
});
