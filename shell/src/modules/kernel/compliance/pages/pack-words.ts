import type { TFunction } from "i18next";
import type { StatusTone } from "@/components/panels";
import i18n from "@/i18n";
import { formatDateTime } from "@/i18n/dates";

/** A pack's period, each end a moment in the person's zone with the zone named (ADR-0050). */
export const packPeriodWords = (from: string, to: string, t: TFunction) =>
  t("evidencePacks.period", { from: formatDateTime(from), to: formatDateTime(to) });

/** Where a pack stands, in words; the tone only repeats it. */
export const packStateWords = (status: string, t: TFunction) =>
  t(`evidencePacks.states.${status}`, { defaultValue: status });

const tones: Record<string, StatusTone> = { requested: "info", building: "info", ready: "success", failed: "danger" };

export const packStateTone = (status: string): StatusTone => tones[status] ?? "neutral";

/** A size in kilobytes or megabytes, in the person's language: "84 kB", "1.2 MB". */
export function sizeWords(bytes: number): string {
  const megabytes = bytes >= 1024 * 1024;
  return new Intl.NumberFormat(i18n.language, {
    style: "unit",
    unit: megabytes ? "megabyte" : "kilobyte",
    maximumFractionDigits: megabytes ? 1 : 0,
  }).format(megabytes ? bytes / (1024 * 1024) : Math.max(1, Math.round(bytes / 1024)));
}

/** The first moment of a calendar day in UTC, and the first of the day after, the bounds a pack's request sends. */
export const startOfUtcDay = (day: string) => `${day}T00:00:00Z`;

export const startOfNextUtcDay = (day: string) =>
  new Date(Date.parse(`${day}T00:00:00Z`) + 24 * 60 * 60 * 1000).toISOString();
