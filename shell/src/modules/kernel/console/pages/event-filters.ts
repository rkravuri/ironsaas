import { startOfDay } from "@/i18n/dates";
import { endOfDay } from "@/modules/kernel/audit/pages/audit-filters";

export const readEvents = "events.events.read";
export const replayEventsPermission = "events.events.replay";

export interface EventFilters {
  type: string;
  recordId: string;
  correlationId: string;
  /** A day, yyyy-mm-dd, from its start in the person's zone. */
  from: string;
  /** A day, yyyy-mm-dd, to its end in the person's zone. */
  to: string;
}

export const emptyEventFilters: EventFilters = { type: "", recordId: "", correlationId: "", from: "", to: "" };

/** The events page's filters in the list grammar (TF-064), the days counted in the person's zone. */
export function eventFilterQuery(filters: EventFilters): string[] {
  const terms: string[] = [];
  const add = (field: string, operator: string, value: string) =>
    terms.push(`filter=${encodeURIComponent(`${field}:${operator}:${value}`)}`);
  if (filters.type !== "") add("type", "eq", filters.type);
  if (filters.recordId.trim() !== "") add("record_id", "eq", filters.recordId.trim());
  if (filters.correlationId.trim() !== "") add("correlation_id", "eq", filters.correlationId.trim());
  if (filters.from !== "") add("occurred_at", "gte", startOfDay(filters.from).toISOString());
  if (filters.to !== "") add("occurred_at", "lt", endOfDay(filters.to).toISOString());
  return terms;
}
