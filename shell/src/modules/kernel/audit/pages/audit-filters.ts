import { startOfDay } from "@/i18n/dates";

export interface Filters {
  eventType: string;
  actorId: string;
  /** Someone who signed in as another person: their events, whoever they acted as (TF-025, TF-064). */
  impersonatorId: string;
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
  impersonatorId: "",
  recordType: "",
  recordId: "",
  source: "",
  from: "",
  to: "",
};

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
  if (filters.impersonatorId !== "") add("impersonator_id", "eq", filters.impersonatorId);
  if (filters.recordType.trim() !== "") add("record_type", "eq", filters.recordType.trim());
  if (filters.recordId.trim() !== "") add("record_id", "eq", filters.recordId.trim());
  if (filters.source !== "") add("source", "eq", filters.source);
  if (filters.from !== "") add("created_at", "ge", startOfDay(filters.from).toISOString());
  if (filters.to !== "") add("created_at", "lt", endOfDay(filters.to).toISOString());
  return terms;
}
