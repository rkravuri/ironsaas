import { z } from "zod";

/** A flag's value for the signed-in person, as /me/context evaluates it in the API (ADR-0035). */
export const FlagValueSchema = z.object({
  key: z.string(),
  type: z.enum(["boolean", "string", "number", "json"]),
  value: z.unknown(),
  reason: z.string(),
});
export type FlagValue = z.infer<typeof FlagValueSchema>;

/** A kill switch that is on, for the administrators' banner: which path, since when, and the operator's reason. */
export const KillSwitchSchema = z.object({
  path: z.string(),
  flag: z.string(),
  since: z.string(),
  reason: z.string().nullable(),
});
export type KillSwitch = z.infer<typeof KillSwitchSchema>;

/**
 * The `flags` part of GET /api/v1/me/context: whether the unit knows any rules (false: the code defaults), every flag of
 * the build evaluated for the person, and the kill switches that are on, sent to administrators only.
 */
export const FlagsContextSchema = z.object({
  known: z.boolean(),
  confirmed_at: z.string().nullable(),
  values: z.array(FlagValueSchema),
  kill_switches: z.array(KillSwitchSchema),
});
export type FlagsContext = z.infer<typeof FlagsContextSchema>;
