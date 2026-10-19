import { z } from "zod";
import { listSchema, request } from "./client";

/** Where a value is set (ADR-0036): the tenant, an organisation unit, a person; the default is the code's. */
export const SettingLevelSchema = z.enum(["default", "tenant", "unit", "user"]);
export type SettingLevel = z.infer<typeof SettingLevelSchema>;

export const SettingTypeSchema = z.enum(["text", "number", "boolean", "choice", "json", "secret_reference"]);
export type SettingType = z.infer<typeof SettingTypeSchema>;

/** A unit by its name and its path of names from the root ("Acme / Operations"). */
export const UnitReferenceSchema = z.object({ id: z.string(), name: z.string(), path: z.string() });

/** Where an effective value comes from: the level, the unit for a unit's value, and who set it when. */
export const SettingSourceSchema = z.object({
  level: SettingLevelSchema,
  unit: UnitReferenceSchema.nullable(),
  changed_by: z.object({ id: z.string(), name: z.string() }).nullable(),
  changed_at: z.string().nullable(),
});
export type SettingSource = z.infer<typeof SettingSourceSchema>;

/** A setting at one place, as the console shows it (TF-058): its declaration, the value there and where it comes from. */
export const SettingViewSchema = z.object({
  key: z.string(),
  module: z.string(),
  type: SettingTypeSchema,
  description: z.string(),
  requirement: z.string(),
  choices: z.array(z.string()).nullable(),
  minimum: z.number().nullable(),
  maximum: z.number().nullable(),
  whole_number: z.boolean(),
  scopes: z.array(SettingLevelSchema),
  default: z.unknown(),
  value: z.unknown(),
  source: SettingSourceSchema,
  set_here: z.boolean(),
});
export type SettingView = z.infer<typeof SettingViewSchema>;

/** GET /api/v1/settings: every setting a place may hold. */
export const SettingsListSchema = z.object({
  scope: z.enum(["tenant", "unit"]),
  unit: UnitReferenceSchema.nullable(),
  data: z.array(SettingViewSchema),
});
export type SettingsList = z.infer<typeof SettingsListSchema>;

/** One change at a place: set or reset, before and after (null for none), who and when. */
export const SettingChangeSchema = z.object({
  id: z.string(),
  kind: z.enum(["set", "reset"]),
  before: z.unknown(),
  after: z.unknown(),
  changed_by_id: z.string(),
  changed_by_name: z.string().nullable(),
  changed_at: z.string(),
});
export type SettingChange = z.infer<typeof SettingChangeSchema>;

/** The `settings` part of /me/context (ADR-0036): each setting the shell may see, its effective value and its level. */
export const ContextSettingsSchema = z.object({
  values: z.array(z.object({ key: z.string(), value: z.unknown(), level: SettingLevelSchema })),
});
export type ContextSettings = z.infer<typeof ContextSettingsSchema>;

/** A place the console reads and changes: the whole tenant, or one unit. A person's own place is "user". */
export type SettingPlace = { scope: "tenant" } | { scope: "unit"; unitId: string } | { scope: "user" };

const placeQuery = (place: SettingPlace) => (place.scope === "unit" ? `scope=unit&unit=${encodeURIComponent(place.unitId)}` : `scope=${place.scope}`);

export const fetchSettings = (place: { scope: "tenant" } | { scope: "unit"; unitId: string }, signal?: AbortSignal) =>
  request(`/api/v1/settings?${placeQuery(place)}`, { schema: SettingsListSchema, ...(signal ? { signal } : {}) });

export const fetchSettingHistory = (key: string, place: { scope: "tenant" } | { scope: "unit"; unitId: string }, signal?: AbortSignal) =>
  request(`/api/v1/settings/${encodeURIComponent(key)}/history?${placeQuery(place)}&page_size=50`, { schema: listSchema(SettingChangeSchema), ...(signal ? { signal } : {}) });

/** Sets a value at a place; the answer is the setting there, now set. A broken rule comes back on the field `value`. */
export const saveSetting = (key: string, place: SettingPlace, value: unknown) =>
  request(`/api/v1/settings/${encodeURIComponent(key)}`, {
    method: "PUT",
    body: { scope: place.scope, unit_id: place.scope === "unit" ? place.unitId : null, value },
    schema: SettingViewSchema,
  });

/** Removes the value at a place (TF-058 AC2); the answer is the setting there with the inherited value. */
export const resetSetting = (key: string, place: SettingPlace) =>
  request(`/api/v1/settings/${encodeURIComponent(key)}?${placeQuery(place)}`, { method: "DELETE", schema: SettingViewSchema });
