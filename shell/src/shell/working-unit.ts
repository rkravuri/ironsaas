import { useCallback, useState } from "react";
import { ApiError } from "@/api/client";
import type { Me, WorkFor, WorkingUnitChoice } from "@/api/identity";
import { useSession } from "@/auth/session-context";

export interface WorkingUnitState {
  busy: boolean;
  /** Why the last choice failed: the API's problem, or nothing more known than that it failed. */
  failure: { error: ApiError | undefined } | undefined;
  choose: (choice: WorkFor) => Promise<void>;
  clearFailure: () => void;
}

/** The one state the header's control and its failure note share: a choice under way, and why the last one failed. */
export function useWorkingUnitChoice(): WorkingUnitState {
  const session = useSession();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<WorkingUnitState["failure"]>();
  const choose = useCallback(
    async (choice: WorkFor) => {
      setBusy(true);
      setFailure(undefined);
      try {
        await session.workFor(choice);
      } catch (error) {
        setFailure({ error: error instanceof ApiError ? error : undefined });
      } finally {
        setBusy(false);
      }
    },
    [session],
  );
  const clearFailure = useCallback(() => setFailure(undefined), []);
  return { busy, failure, choose, clearFailure };
}

/**
 * Whether the header offers a choice of unit (ADR-0038): only with more than one unit to choose, and not while acting
 * for someone, whose rights in full the lists then show.
 */
export function offersWorkingUnits(me: Me | undefined): boolean {
  return me !== undefined && (me.working_units?.units.length ?? 0) > 1 && !me.acting_for;
}

/** A choice placed on the tree: how many of the other choices are above it, and the path above it when none is. */
export interface PlacedChoice {
  unit: WorkingUnitChoice;
  indent: number;
  /** The names above the unit, root first, for a unit none of whose ancestors is a choice; empty for the root. */
  above: string[];
}

/** The choices as the menu shows them: in tree order, indented under the choices above them (the API sends tree order). */
export function placeChoices(units: readonly WorkingUnitChoice[]): PlacedChoice[] {
  const ids = new Set(units.map((unit) => unit.id));
  return units.map((unit) => {
    const ancestors = unit.path.slice(0, -1);
    const indent = ancestors.filter((step) => ids.has(step.id)).length;
    return { unit, indent, above: indent === 0 ? ancestors.map((step) => step.name) : [] };
  });
}

/** What the lists keep to, in words for a page's description: one unit and the units below it, or all of them. */
export function workingScope(me: Me | undefined): { kind: "unit"; name: string } | { kind: "all" } | undefined {
  if (!offersWorkingUnits(me)) {
    return undefined;
  }
  if (me?.all_units) {
    return { kind: "all" };
  }
  return me?.working_unit ? { kind: "unit", name: me.working_unit.name } : undefined;
}
