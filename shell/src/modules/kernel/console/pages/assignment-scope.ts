import type { TFunction } from "i18next";
import type { Assignment } from "@/api/authorization";

/** Where an assignment applies, in words: the whole tenant, one unit, a unit and the units below it, or one record. */
export function scopeLabel(assignment: Assignment, t: TFunction): string {
  return t(`access.scopes.${assignment.scope_kind}`);
}

/** The unit an assignment names, by its path of names from the top the reader can see: Acme / Sales / North. */
export function unitPathLabel(assignment: Assignment): string {
  return assignment.unit_path.map((step) => step.name).join(" / ");
}
