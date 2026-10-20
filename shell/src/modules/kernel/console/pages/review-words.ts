import type { TFunction } from "i18next";
import type { ReviewProgress } from "@/api/authorization";

/** A due day for people, in the reader's language. */
export const dueLabel = (day: string, language: string) => new Date(`${day}T00:00:00`).toLocaleDateString(language, { dateStyle: "medium" });

/** Progress in words: how many are decided of how many, and how many are late. */
export function progressLabel(progress: ReviewProgress, t: TFunction): string {
  const decided = t("reviews.progress.decided", { decided: progress.decided, total: progress.total });
  return progress.overdue > 0 ? `${decided} ${t("reviews.progress.overdue", { count: progress.overdue })}` : decided;
}
