import type { TFunction } from "i18next";
import i18n from "@/i18n";
import { formatDay } from "@/i18n/dates";

/** A review's period in words: "Sep 1, 2026 to Sep 30, 2026", the days as they are in every zone. */
export const periodWords = (from: string, to: string, t: TFunction) =>
  t("auditReviews.period", { from: formatDay(from), to: formatDay(to) });

/** A review's outcome in words, never a colour alone. */
export const outcomeWords = (outcome: string, t: TFunction) =>
  t(`auditReviews.outcomes.${outcome}`, { defaultValue: outcome });

/** A calendar month by its first day ("2026-09-01"): "Sep 2026". */
export const monthWords = (firstDay: string) =>
  new Intl.DateTimeFormat(i18n.language, { month: "short", year: "numeric", timeZone: "UTC" }).format(
    new Date(`${firstDay}T00:00:00Z`),
  );
