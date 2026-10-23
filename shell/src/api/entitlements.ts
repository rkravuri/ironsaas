import { z } from "zod";
import { request } from "./client";
import { FlagsContextSchema } from "./flags";
import { ContextSettingsSchema } from "./settings";

/** What applies to a module or a feature now (TF-040): the status after dates, grace, seats and the governing module. */
export const EntitlementStatusSchema = z.enum(["enabled", "read_only", "disabled"]);
export type EntitlementStatus = z.infer<typeof EntitlementStatusSchema>;

/** The version the unit runs (TF-218, ADR-0052): a release's, or "development" with development true on a laptop. */
export const ReleaseSchema = z.object({ version: z.string(), development: z.boolean() });
export type Release = z.infer<typeof ReleaseSchema>;

/** One module or feature as /me/context answers it, for the signed-in person. */
export const ContextEntrySchema = z.object({ code: z.string(), status: EntitlementStatusSchema, reason: z.string() });
export type ContextEntry = z.infer<typeof ContextEntrySchema>;

/**
 * GET /api/v1/me/context (ADR-0034): what the shell reads with the session and every 30 seconds, in one call: the
 * entitlements, the flags (ADR-0035) and the settings the shell may see (ADR-0036). A flags or settings part that is
 * missing or does not parse leaves those on their defaults and the entitlements as they came (TF-054 AC3).
 */
export const ContextSchema = z.object({
  entitlements: z.object({
    revision: z.number(),
    plan: z.string().nullable(),
    confirmed_at: z.string().nullable(),
    stale: z.boolean(),
    modules: z.array(ContextEntrySchema),
    features: z.array(ContextEntrySchema),
  }),
  flags: FlagsContextSchema.optional().catch(undefined),
  settings: ContextSettingsSchema.optional().catch(undefined),
  /** The version the unit runs, or a development build on a laptop (TF-218, ADR-0052). */
  release: ReleaseSchema.optional().catch(undefined),
});
export type Context = z.infer<typeof ContextSchema>;

export const fetchContext = (signal?: AbortSignal) =>
  request("/api/v1/me/context", { schema: ContextSchema, ...(signal ? { signal } : {}) });

const SeatsSchema = z.object({ limit: z.number().nullable(), assigned: z.number(), remaining: z.number().nullable() });

export const SubscriptionModuleSchema = z.object({
  code: z.string(),
  module: z.string(),
  subscription_type: z.enum(["per_person", "per_transaction", "platform"]),
  status: EntitlementStatusSchema,
  reason: z.string(),
  starts_on: z.string().nullable(),
  ends_on: z.string().nullable(),
  grace_ends_on: z.string().nullable(),
  seats: SeatsSchema.nullable(),
});
export type SubscriptionModule = z.infer<typeof SubscriptionModuleSchema>;

export const SubscriptionFeatureSchema = z.object({
  code: z.string(),
  description: z.string(),
  module: z.string(),
  licence: z.string().nullable(),
  status: EntitlementStatusSchema,
  reason: z.string(),
  starts_on: z.string().nullable(),
  ends_on: z.string().nullable(),
  grace_ends_on: z.string().nullable(),
});
export type SubscriptionFeature = z.infer<typeof SubscriptionFeatureSchema>;

/** GET /api/v1/subscription: the tenant's licensed modules and features, with status, dates, reason and seats. Read only. */
export const SubscriptionSchema = z.object({
  plan: z.string().nullable(),
  revision: z.number(),
  confirmed_at: z.string().nullable(),
  stale: z.boolean(),
  grace_days: z.number(),
  modules: z.array(SubscriptionModuleSchema),
  features: z.array(SubscriptionFeatureSchema),
});
export type Subscription = z.infer<typeof SubscriptionSchema>;

export const fetchSubscription = (signal?: AbortSignal) =>
  request("/api/v1/subscription", { schema: SubscriptionSchema, ...(signal ? { signal } : {}) });
