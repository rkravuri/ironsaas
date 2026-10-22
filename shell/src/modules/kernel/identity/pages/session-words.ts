import type { TFunction } from "i18next";
import type { SessionRecord } from "@/api/identity";

/** A session's device in words: "Chrome on Windows", or what is known of it (TF-022). */
export function deviceOf(session: Pick<SessionRecord, "browser" | "platform">, t: TFunction): string {
  if (session.browser && session.platform) {
    return t("sessions.device.both", { browser: session.browser, platform: session.platform });
  }
  return session.browser ?? session.platform ?? t("sessions.device.unknown");
}

/** A session's place in words: the city and the country, or what is known of it; never finer (ADR-0049). */
export function placeOf(session: Pick<SessionRecord, "city" | "country">, t: TFunction): string {
  if (session.city && session.country) {
    return t("sessions.place.both", { city: session.city, country: session.country });
  }
  return session.country ?? session.city ?? t("sessions.place.unknown");
}
