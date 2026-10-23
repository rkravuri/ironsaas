import type { TFunction } from "i18next";
import type { StatusTone } from "@/components/panels";

/** An area of sensitive configuration in words (ADR-0052). */
export const areaWords = (area: string, t: TFunction) => t(`changes.areas.${area}`, { defaultValue: area });

/** Where a change stands, in words; the tone only repeats it (brief section 5). */
export const changeStateWords = (status: string, t: TFunction) =>
  t(`changes.states.${status}`, { defaultValue: status });

const tones: Record<string, StatusTone> = {
  pending: "info",
  applied: "success",
  rejected: "danger",
  withdrawn: "neutral",
  expired: "warning",
};

export const changeStateTone = (status: string): StatusTone => tones[status] ?? "neutral";

/** One value of a state as text: words for nothing and for yes or no, lists joined, anything deeper as compact JSON. */
export function valueWords(value: unknown, t: TFunction): string {
  if (value === undefined || value === null) {
    return t("changes.values.none");
  }
  if (typeof value === "boolean") {
    return t(value ? "changes.values.yes" : "changes.values.no");
  }
  if (typeof value === "string") {
    return value === "" ? t("changes.values.empty") : value;
  }
  if (typeof value === "number") {
    return String(value);
  }
  if (Array.isArray(value)) {
    if (value.length === 0) {
      return t("changes.values.emptyList");
    }
    return value.every((item) => typeof item !== "object" || item === null)
      ? value.map((item) => valueWords(item, t)).join(", ")
      : JSON.stringify(value);
  }
  return JSON.stringify(value);
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);

export interface ChangeRow {
  field: string;
  before: unknown;
  after: unknown;
  changed: boolean;
}

/**
 * The rows of a before-and-after table: one per field of either state, in the order the states name them; a state that
 * is not an object is one row, "value". Nothing before means a thing is made, nothing after that it is removed.
 */
export function changeRows(before: unknown, after: unknown): ChangeRow[] {
  const objects = (isRecord(before) || before === null) && (isRecord(after) || after === null);
  if (!objects || (before === null && after === null)) {
    return [{ field: "value", before, after, changed: JSON.stringify(before) !== JSON.stringify(after) }];
  }
  const was = isRecord(before) ? before : {};
  const will = isRecord(after) ? after : {};
  const fields = [...new Set([...Object.keys(was), ...Object.keys(will)])];
  return fields.map((field) => ({
    field,
    before: before === null ? undefined : was[field],
    after: after === null ? undefined : will[field],
    changed: JSON.stringify(was[field]) !== JSON.stringify(will[field]),
  }));
}
