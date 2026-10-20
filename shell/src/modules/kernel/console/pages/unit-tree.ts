import type { TreeNodeData } from "@mantine/core";
import type { Kind, Kinds, Unit } from "@/api/authorization";

/**
 * The API's depth-first list as Mantine's tree data, keeping the API's sibling order; each node's value is the unit id.
 * A reader of part of the tree gets units whose parent is not in the list (ADR-0031): those start the tree.
 */
export function toTreeData(units: readonly Unit[]): TreeNodeData[] {
  const present = new Set(units.map((unit) => unit.id));
  const children = new Map<string | null, Unit[]>();
  for (const unit of units) {
    const parent = unit.parent_id !== null && present.has(unit.parent_id) ? unit.parent_id : null;
    children.set(parent, [...(children.get(parent) ?? []), unit]);
  }
  const build = (parent: string | null): TreeNodeData[] =>
    (children.get(parent) ?? []).map((unit) => {
      const below = build(unit.id);
      return below.length > 0
        ? { value: unit.id, label: unit.name, children: below }
        : { value: unit.id, label: unit.name };
    });
  return build(null);
}

/** The unit's id and the ids of every unit below it. */
export function subtreeIds(units: readonly Unit[], id: string): Set<string> {
  const ids = new Set<string>([id]);
  // The list is depth first, so a parent always comes before its children.
  for (const unit of units) {
    if (unit.parent_id !== null && ids.has(unit.parent_id)) {
      ids.add(unit.id);
    }
  }
  return ids;
}

/** The units from the root down to this one. */
export function pathOf(units: readonly Unit[], id: string): Unit[] {
  const byId = new Map(units.map((unit) => [unit.id, unit]));
  const path: Unit[] = [];
  for (
    let current = byId.get(id);
    current !== undefined;
    current = current.parent_id === null ? undefined : byId.get(current.parent_id)
  ) {
    path.unshift(current);
  }
  return path;
}

export function kindOf(kinds: Kinds | undefined, code: string): Kind | undefined {
  return kinds?.data.find((kind) => kind.code === code);
}

/** The kinds a unit of this kind may hold, in the provider's order. */
export function childKinds(kinds: Kinds | undefined, parentKind: string): Kind[] {
  const allowed = kindOf(kinds, parentKind)?.child_kinds ?? [];
  return (kinds?.data ?? []).filter((kind) => allowed.includes(kind.code));
}

/**
 * Where a unit may move, as the API decides it (ADR-0030): an active unit outside the unit's own subtree whose kind may
 * hold the unit's kind, other than its present parent. The root never moves.
 */
export function moveTargets(units: readonly Unit[], unit: Unit, kinds: Kinds | undefined): Unit[] {
  if (unit.parent_id === null) {
    return [];
  }
  const own = subtreeIds(units, unit.id);
  return units.filter(
    (candidate) =>
      candidate.status === "active" &&
      !own.has(candidate.id) &&
      candidate.id !== unit.parent_id &&
      (kindOf(kinds, candidate.kind)?.child_kinds.includes(unit.kind) ?? false),
  );
}

/** A unit by its path of names, for a choice in a list: Acme / Sales / North. */
export function pathLabel(units: readonly Unit[], id: string): string {
  return pathOf(units, id)
    .map((unit) => unit.name)
    .join(" / ");
}

/**
 * The units another may merge into, as the API decides it (ADR-0038): active units outside its own subtree. The root
 * never merges, nor does a unit merged already. Kinds and depth the API checks, and says so on the preview.
 */
export function mergeTargets(units: readonly Unit[], unit: Unit): Unit[] {
  if (unit.parent_id === null || unit.merged_into_id) {
    return [];
  }
  const own = subtreeIds(units, unit.id);
  return units.filter((candidate) => candidate.status === "active" && !own.has(candidate.id));
}
