import i18n from "./index";

/**
 * Every time the shell shows is in the person's time zone, with the zone named (TF-065 AC2, ADR-0050): "Oct 22, 2026,
 * 2:30 PM GMT+5:30". The server stores UTC and never takes a client's time; the zone is the person's setting
 * `shell.time_zone`, or this browser's when they have not chosen one. A calendar day with no time of its own, such as
 * a due date or a day of a summary kept in UTC, is shown as that day, in no zone.
 */
let chosenZone: string | undefined;

/** The browser's own zone: the default when the person has not chosen one. */
export function browserTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

/** Whether the browser knows this zone; an unknown one falls back to the browser's, so no time fails to show. */
export function isTimeZone(zone: string): boolean {
  try {
    new Intl.DateTimeFormat("en", { timeZone: zone });
    return true;
  } catch {
    return false;
  }
}

/** Set from the person's setting by the shell's frame; an empty value follows the browser. */
export function setTimeZone(zone: string | undefined): void {
  chosenZone = zone !== undefined && zone !== "" && isTimeZone(zone) ? zone : undefined;
}

export function currentTimeZone(): string {
  return chosenZone ?? browserTimeZone();
}

type Value = string | number | Date;

const toDate = (value: Value) => (value instanceof Date ? value : new Date(value));

/** A moment with its date, its time and the zone: "Oct 22, 2026, 2:30 PM GMT+5:30". */
export function formatDateTime(value: Value): string {
  return new Intl.DateTimeFormat(i18n.language, {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
    timeZone: currentTimeZone(),
    timeZoneName: "short",
  }).format(toDate(value));
}

/** A moment's time of day with the zone: "2:30 PM GMT+5:30", for a moment today, such as a session's warning. */
export function formatTime(value: Value): string {
  return new Intl.DateTimeFormat(i18n.language, {
    hour: "numeric",
    minute: "2-digit",
    timeZone: currentTimeZone(),
    timeZoneName: "short",
  }).format(toDate(value));
}

/** A moment's date in the person's zone, without the time: "Oct 22, 2026". */
export function formatDate(value: Value): string {
  return new Intl.DateTimeFormat(i18n.language, { dateStyle: "medium", timeZone: currentTimeZone() }).format(
    toDate(value),
  );
}

/** A calendar day with no time of its own ("2026-10-22"), shown as that same day in every zone. */
export function formatDay(day: string): string {
  return new Intl.DateTimeFormat(i18n.language, { dateStyle: "medium", timeZone: "UTC" }).format(
    new Date(`${day.slice(0, 10)}T00:00:00Z`),
  );
}

/** Every zone the browser knows, for the person's choice. */
export function timeZones(): string[] {
  const supported = (Intl as unknown as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
  return supported ? supported("timeZone") : [browserTimeZone()];
}

/**
 * The first instant of a day in the person's zone, for a search by date: "2026-10-22" in Asia/Kolkata is 2026-10-21
 * 18:30 UTC. The server compares UTC; the person thinks in their own days.
 */
export function startOfDay(day: string): Date {
  const utc = new Date(`${day.slice(0, 10)}T00:00:00Z`);
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone: currentTimeZone(),
    hourCycle: "h23",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  }).formatToParts(utc);
  const part = (type: Intl.DateTimeFormatPartTypes) => Number(parts.find((p) => p.type === type)?.value ?? 0);
  const seenThere = Date.UTC(
    part("year"),
    part("month") - 1,
    part("day"),
    part("hour"),
    part("minute"),
    part("second"),
  );
  return new Date(utc.getTime() - (seenThere - utc.getTime()));
}
