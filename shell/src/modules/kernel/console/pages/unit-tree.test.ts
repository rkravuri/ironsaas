import { describe, expect, it } from "vitest";
import type { Kinds, Unit } from "@/api/authorization";
import { childKinds, moveTargets, pathLabel, subtreeIds, toTreeData } from "./unit-tree";

const unit = (id: string, name: string, parent: string | null, extra: Partial<Unit> = {}): Unit => ({
  id,
  name,
  code: name.toLowerCase(),
  kind: parent === null ? "root" : "unit",
  parent_id: parent,
  depth: 0,
  status: "active",
  created_at: "2026-10-19T08:00:00Z",
  updated_at: "2026-10-19T08:00:00Z",
  version: 1,
  ...extra,
});

// Depth first, as the API lists them.
const units = [
  unit("root", "Acme", null),
  unit("finance", "Finance", "root"),
  unit("sales", "Sales", "root"),
  unit("north", "North", "sales"),
  unit("city", "City", "north"),
  unit("closed", "Closed", "root", { status: "inactive" }),
];

const kinds: Kinds = {
  root_kind: "root",
  data: [
    { code: "root", name: "Root", child_kinds: ["unit"] },
    { code: "unit", name: "Unit", child_kinds: ["unit"] },
  ],
};

describe("the unit tree", () => {
  it("starts at the units whose parent the reader cannot see (ADR-0031)", () => {
    // A reader of Sales with its children gets Sales and below; Sales's parent is not in the list.
    const tree = toTreeData(units.filter((candidate) => ["sales", "north", "city"].includes(candidate.id)));
    expect(tree.map((node) => node.value)).toEqual(["sales"]);
    expect(tree[0]?.children?.map((node) => node.value)).toEqual(["north"]);
    expect(pathLabel(units.slice(2, 5), "city")).toBe("Sales / North / City");
  });

  it("nests the API's list under its parents and keeps the sibling order", () => {
    const tree = toTreeData(units);
    expect(tree.map((node) => node.value)).toEqual(["root"]);
    expect(tree[0]?.children?.map((node) => node.value)).toEqual(["finance", "sales", "closed"]);
    expect(tree[0]?.children?.[1]?.children?.[0]?.children?.[0]?.value).toBe("city");
    expect(tree[0]?.children?.[0]?.children).toBeUndefined();
  });

  it("finds a unit's subtree and its path of names", () => {
    expect([...subtreeIds(units, "sales")]).toEqual(["sales", "north", "city"]);
    expect(pathLabel(units, "city")).toBe("Acme / Sales / North / City");
  });

  it("offers as move targets the active units outside the subtree, never the present parent", () => {
    const north = units.find((u) => u.id === "north")!;
    expect(moveTargets(units, north, kinds).map((u) => u.id)).toEqual(["root", "finance"]);
    expect(moveTargets(units, units[0]!, kinds)).toEqual([]);
  });

  it("offers as child kinds what the parent's kind may hold", () => {
    expect(childKinds(kinds, "root").map((k) => k.code)).toEqual(["unit"]);
    expect(childKinds(kinds, "nothing")).toEqual([]);
  });
});
