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

const stub = (permissions: string[], extra = {}) =>
  stubFetch({
    "GET /api/v1/me": () => json(me(permissions)),
    "GET /api/v1/organisation-units": () => json(tree),
    "GET /api/v1/organisation-kinds": () => json(kinds),
    ...extra,
  });

const record = () => screen.getByText("Path").closest("table")!.parentElement!;

/** A tree item by its own label: an item's accessible name also holds the names of the items below it. */
const item = (label: string) => within(screen.getByRole("tree")).getByText(label).closest<HTMLElement>("[role=treeitem]")!;

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

  it("is read only without the manage permission", async () => {
    stub(["authz.units.read"]);
    renderWithProviders(<UnitsPage />);

    await screen.findByRole("tree");
    await waitFor(() => expect(record()).toHaveTextContent("Acme"));
    expect(screen.queryByRole("button", { name: "Add a unit here" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Rename" })).toBeNull();
  });
});
