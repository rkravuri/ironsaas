import type { TFunction } from "i18next";
import type { RecordStatus, StatusTone } from "@/components/panels";

const tones: Record<string, StatusTone> = {
  active: "success",
  invited: "info",
  locked: "warning",
  inactive: "neutral",
  disabled: "neutral",
};

/** The API's status as a word with a tone; an unknown status stays a word in the neutral tone. */
export function userStatus(status: string, t: TFunction): RecordStatus {
  return { label: t(`users.statuses.${status}`, { defaultValue: status }), tone: tones[status] ?? "neutral" };
}
