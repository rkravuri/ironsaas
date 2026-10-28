import { startOfDay } from "@/i18n/dates";

export interface Filters {
  /** One event type, or several separated by commas, as a security alert's link names its rule's (ADR-0056). */
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
  /** The events' target, such as a login identifier: a security alert's group (ADR-0056). */
  target: string;
  /** The client's address the events recorded: a security alert's group. */
  address: string;
  /** A moment, as an alert's link gives it: its first event, included. */
  since: string;
  /** A moment: the alert's last event, included. */
  until: string;
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
  target: "",
  address: "",
  since: "",
  until: "",
};

/**
 * The filters a link opens the page with (ADR-0056): a security alert links to the events behind it by their types,
 * its group (the actor, the target, the address or the record) and the moments of its first and last event.
 */
export function filtersFromLink(parameters: URLSearchParams): Filters {
  const read = (name: string) => parameters.get(name)?.trim() ?? "";
  return {
    ...emptyFilters,
    eventType: read("event_type"),
    actorId: read("actor_id"),
    recordId: read("record_id"),
    target: read("target"),
    address: read("address"),
    since: read("since"),
    until: read("until"),
  };
}

/** Whether the filters hold what only a link gives: an alert's group or its moments. */
export const fromAnAlert = (filters: Filters): boolean =>
  filters.target !== "" || filters.address !== "" || filters.since !== "" || filters.until !== "";

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
  const types = filters.eventType
    .split(",")
    .map((type) => type.trim())
    .filter((type) => type !== "");
  if (types.length === 1) add("event_type", "eq", types[0] ?? "");
  if (types.length > 1) add("event_type", "in", types.join(","));
  if (filters.actorId !== "") add("actor_id", "eq", filters.actorId);
  const signedInBy = signedInByTerm(filters.signedInBy);
  if (signedInBy) add(signedInBy.field, "eq", signedInBy.id);
  if (filters.recordType.trim() !== "") add("record_type", "eq", filters.recordType.trim());
  if (filters.recordId.trim() !== "") add("record_id", "eq", filters.recordId.trim());
  if (filters.source !== "") add("source", "eq", filters.source);
  if (filters.target.trim() !== "") add("target", "eq", filters.target.trim());
  if (filters.from !== "") add("created_at", "gte", startOfDay(filters.from).toISOString());
  if (filters.to !== "") add("created_at", "lt", endOfDay(filters.to).toISOString());
  if (filters.since !== "") add("created_at", "gte", filters.since);
  if (filters.until !== "") add("created_at", "lte", filters.until);
  // The address is a detail of the events, which the search takes beside the list grammar.
  if (filters.address.trim() !== "") terms.push(`address=${encodeURIComponent(filters.address.trim())}`);
  return terms;
}
