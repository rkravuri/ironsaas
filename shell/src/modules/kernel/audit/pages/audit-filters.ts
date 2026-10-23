import { startOfDay } from "@/i18n/dates";

export interface Filters {
  eventType: string;
  actorId: string;
  /**
   * Someone who signed in as another person or came in through break-glass, as "field:id": an operator or a person by
   * <c>impersonator_id</c>, a break-glass account, which acts as itself, by <c>actor_id</c> (TF-025, ADR-0049).
   */
  signedInBy: string;
  recordType: string;
  recordId: string;
  source: string;
  /** A day, yyyy-mm-dd, from its start in the person's zone. */
  from: string;
  /** A day, yyyy-mm-dd, to its end in the person's zone. */
  to: string;
}

export const emptyFilters: Filters = {
  eventType: "",
  actorId: "",
  signedInBy: "",
  recordType: "",
  recordId: "",
  source: "",
  from: "",
  to: "",
};

/** The filter's field and id from its "field:id" value; nothing when none is chosen. */
export function signedInByTerm(value: string): { field: "impersonator_id" | "actor_id"; id: string } | undefined {
  const [field, id] = value.split(":");
  return (field === "impersonator_id" || field === "actor_id") && id ? { field, id } : undefined;
}

/** The day after a yyyy-mm-dd day, as yyyy-mm-dd. */
export function nextDay(day: string): string {
  const [year, month, date] = day.split("-").map(Number);
  return new Date(Date.UTC(year ?? 1970, (month ?? 1) - 1, (date ?? 1) + 1)).toISOString().slice(0, 10);
}

/**
 * The end of a day in the person's zone: the start of the next. A day is 23 or 25 hours when the clocks change, so it is
 * never the start plus 24 hours.
 */
export const endOfDay = (day: string): Date => startOfDay(nextDay(day));

/** The page's filters in the list grammar (TF-064), the days counted in the person's zone. */
export function filterQuery(filters: Filters): string[] {
  const terms: string[] = [];
  const add = (field: string, operator: string, value: string) =>
    terms.push(`filter=${encodeURIComponent(`${field}:${operator}:${value}`)}`);
  if (filters.eventType.trim() !== "") add("event_type", "eq", filters.eventType.trim());
  if (filters.actorId !== "") add("actor_id", "eq", filters.actorId);
  const signedInBy = signedInByTerm(filters.signedInBy);
  if (signedInBy) add(signedInBy.field, "eq", signedInBy.id);
  if (filters.recordType.trim() !== "") add("record_type", "eq", filters.recordType.trim());
  if (filters.recordId.trim() !== "") add("record_id", "eq", filters.recordId.trim());
  if (filters.source !== "") add("source", "eq", filters.source);
  if (filters.from !== "") add("created_at", "ge", startOfDay(filters.from).toISOString());
  if (filters.to !== "") add("created_at", "lt", endOfDay(filters.to).toISOString());
  return terms;
}
