import { settingDefaults, type SettingKey, type SettingValues } from "@/api/setting-keys";
import type { ContextSettings, SettingLevel } from "@/api/settings";
import { useSession } from "./session-context";

/** A setting's effective value for the signed-in person, and the level it comes from. */
export interface EffectiveSetting {
  value: unknown;
  level: SettingLevel;
}

export type EffectiveSettings = ReadonlyMap<string, EffectiveSetting>;

/** The settings of an answer of /me/context; without one, none, and every setting has its default. */
export function settingsFrom(context: ContextSettings | undefined): EffectiveSettings {
  return new Map((context?.values ?? []).map((entry) => [entry.key, { value: entry.value, level: entry.level }]));
}

/**
 * The one hook for settings (TF-059, ADR-0036): the effective value for the signed-in person, typed by the keys
 * `make catalogue` writes from the declarations, so only a declared key the shell may see is accepted. Until
 * /me/context answers, or when it cannot, or when a value is not of the declared type, the default in code applies.
 */
export function useSetting<K extends SettingKey>(key: K): SettingValues[K] {
  const fallback = settingDefaults[key];
  const entry = useSession().settings.get(key);
  return entry !== undefined && typeof entry.value === typeof fallback ? (entry.value as SettingValues[K]) : fallback;
}
