import type { TFunction } from "i18next";
import type { ReviewProgress } from "@/api/authorization";
import { formatDay } from "@/i18n/dates";

/** A due day for people: a calendar day, the same in every zone. */
export const dueLabel = (day: string) => formatDay(day);

/** Progress in words: how many are decided of how many, and how many are late. */
export function progressLabel(progress: ReviewProgress, t: TFunction): string {
  const decided = t("reviews.progress.decided", { decided: progress.decided, total: progress.total });
  return progress.overdue > 0 ? `${decided} ${t("reviews.progress.overdue", { count: progress.overdue })}` : decided;
}
