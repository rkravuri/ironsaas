import type { TFunction } from "i18next";
import type { ReviewItem, ReviewProgress } from "@/api/authorization";
import { formatDay } from "@/i18n/dates";

/** A due day for people: a calendar day, the same in every zone. */
export const dueLabel = (day: string) => formatDay(day);

/** Progress in words: how many are decided of how many, and how many are late. */
export function progressLabel(progress: ReviewProgress, t: TFunction): string {
  const decided = t("reviews.progress.decided", { decided: progress.decided, total: progress.total });
  return progress.overdue > 0 ? `${decided} ${t("reviews.progress.overdue", { count: progress.overdue })}` : decided;
}

/** What an item reviews, in words: its role, or a visibility tag by its name and code (ADR-0057). */
export function reviewedWords(item: ReviewItem, t: TFunction): string {
  return item.tag ? t("reviews.items.tagWords", { name: item.tag.name, code: item.tag.code }) : (item.role_name ?? "");
}

/** The visibility tags the item's person held when the campaign started (TF-241 AC7), by their codes. */
export function heldTagsWords(item: ReviewItem, t: TFunction): string {
  return item.held_tags.length === 0 ? t("reviews.items.noTags") : item.held_tags.map((tag) => tag.code).join(", ");
}
