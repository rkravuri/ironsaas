import type { TFunction } from "i18next";
import type { SettingView } from "@/api/settings";
import { formatDate } from "@/i18n/dates";
import { areaWords } from "./change-words";

/** The catalogue key of a setting's words: i18next reads dots as nesting, so the key's dots become underscores. */
export const settingSlug = (key: string) => key.replaceAll(".", "_");

export const settingName = (setting: SettingView, t: TFunction) =>
  t(`settings.names.${settingSlug(setting.key)}`, { defaultValue: setting.key });

const asText = (value: unknown): string => (typeof value === "string" ? value : JSON.stringify(value));

/** The signing policy per document type (ADR-0051), edited per type and read as a sentence per type. */
export const signingPoliciesKey = "compliance.signing.policies";

function policyWords(value: unknown, t: TFunction): string {
  const entries = typeof value === "object" && value !== null ? Object.entries(value as Record<string, unknown>) : [];
  if (entries.length === 0) {
    return t("signingPolicy.defaults");
  }
  return entries
    .map(([type, policy]) => {
      const p = (policy ?? {}) as { required?: boolean; reauthenticate?: boolean; meanings?: string[] };
      return t("signingPolicy.summary", {
        type: t(`signingPolicy.types.${type}`, { defaultValue: type }),
        required: t(
          p.required === false
            ? "signingPolicy.notRequiredWord"
            : p.required
              ? "signingPolicy.requiredWord"
              : "signingPolicy.defaultWord",
        ),
        password: t(p.reauthenticate === false ? "signingPolicy.noPasswordWord" : "signingPolicy.passwordWord"),
      });
    })
    .join(" ");
}

/** The four-eyes lists of areas (ADR-0052): which changes wait for a second administrator, and which approvals are signed. */
export const areaListKeys: readonly string[] = ["changes.four_eyes.areas", "changes.four_eyes.signed_areas"];

const areaListWords = (value: unknown, t: TFunction): string => {
  const areas = Array.isArray(value) ? value.filter((area): area is string => typeof area === "string") : [];
  return areas.length === 0 ? t("settings.noAreas") : areas.map((area) => areaWords(area, t)).join(", ");
};

/** The origins allowed to call the API from a browser (TF-070 AC2, ADR-0053): a list, edited one origin at a time. */
export const originListKey = "protection.cors.allowed_origins";

const originListWords = (value: unknown, t: TFunction): string => {
  const origins = Array.isArray(value) ? value.filter((origin): origin is string => typeof origin === "string") : [];
  return origins.length === 0 ? t("settings.noOrigins") : origins.join(", ");
};

/** A value in words: an amount with its unit, a choice by its word, on or off, a path, or the JSON as it is. */
export function displayValue(setting: SettingView, value: unknown, t: TFunction): string {
  if (value === null || value === undefined) {
    return t("settings.none");
  }
  if (setting.key === signingPoliciesKey) {
    return policyWords(value, t);
  }
  if (areaListKeys.includes(setting.key)) {
    return areaListWords(value, t);
  }
  if (setting.key === originListKey) {
    return originListWords(value, t);
  }
  switch (setting.type) {
    case "number":
      return t(`settings.amounts.${settingSlug(setting.key)}`, { count: Number(value), defaultValue: asText(value) });
    case "boolean":
      return t(value === true ? "settings.boolean.true" : "settings.boolean.false");
    case "choice":
      return t(`settings.choices.${settingSlug(setting.key)}.${asText(value)}`, { defaultValue: asText(value) });
    default:
      return asText(value);
  }
}

const day = (iso: string | null) => (iso === null ? "" : formatDate(iso));

/** Where the value at this place comes from, in a sentence (TF-058): the default, here, a unit above, or the organisation. */
export function sourceSentence(setting: SettingView, t: TFunction): string {
  const { source } = setting;
  const name = source.changed_by?.name ?? t("settings.source.someone");
  const date = day(source.changed_at);
  if (source.level === "default") {
    return t("settings.source.default");
  }
  if (setting.set_here) {
    return t("settings.source.here", { name, date });
  }
  if (source.level === "unit" && source.unit !== null) {
    return t("settings.source.unit", { unit: source.unit.path, name, date });
  }
  return t("settings.source.tenant", { name, date });
}
