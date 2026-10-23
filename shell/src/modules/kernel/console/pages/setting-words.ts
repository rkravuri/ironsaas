import type { TFunction } from "i18next";
import type { SettingView } from "@/api/settings";
import { formatDate } from "@/i18n/dates";

/** The catalogue key of a setting's words: i18next reads dots as nesting, so the key's dots become underscores. */
export const settingSlug = (key: string) => key.replaceAll(".", "_");

export const settingName = (setting: SettingView, t: TFunction) =>
  t(`settings.names.${settingSlug(setting.key)}`, { defaultValue: setting.key });

const asText = (value: unknown): string => (typeof value === "string" ? value : JSON.stringify(value));

/** A value in words: an amount with its unit, a choice by its word, on or off, a path, or the JSON as it is. */
export function displayValue(setting: SettingView, value: unknown, t: TFunction): string {
  if (value === null || value === undefined) {
    return t("settings.none");
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
