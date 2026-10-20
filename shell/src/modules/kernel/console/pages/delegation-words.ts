import type { DelegationStatus } from "@/api/authorization";
import type { StatusTone } from "@/components/panels";

/** A status in a meaning and in words, never colour alone (brief section 5). */
export const statusTone: Record<DelegationStatus, StatusTone> = { scheduled: "info", active: "success", ended: "neutral", expired: "neutral" };

/** Whether a delegation still counts, now or later: only those can be ended. */
export const isRunning = (status: DelegationStatus) => status === "scheduled" || status === "active";

/** A day as the date input gives it (yyyy-mm-dd), in this browser's time zone. */
export function today(now: Date = new Date()): string {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

const startOfDay = (day: string) => {
  const [year, month, date] = day.split("-").map(Number);
  return new Date(year ?? 1970, (month ?? 1) - 1, date ?? 1);
};

/**
 * The period of a delegation from two days in the person's own time zone: from the start of the first day, or from now
 * when that is today, to the end of the last day. Instants go to the API; it compares them with its own clock.
 */
export function periodOf(from: string, until: string, now: Date = new Date()): { starts_at: string; ends_at: string } {
  const start = startOfDay(from);
  const end = startOfDay(until);
  end.setDate(end.getDate() + 1);
  const starts = start.getTime() < now.getTime() ? now : start;
  return { starts_at: starts.toISOString(), ends_at: end.toISOString() };
}

/** A period for people: the first day and the last, in the reader's language. */
export function periodLabel(startsAt: string, endsAt: string, language: string): { from: string; until: string } {
  const day = (value: Date) => value.toLocaleDateString(language, { dateStyle: "medium" });
  // The end is the first instant after the period; the last day is the one before it.
  const last = new Date(new Date(endsAt).getTime() - 1);
  return { from: day(new Date(startsAt)), until: day(last) };
}
