import { z } from "zod";
import { request } from "./client";
import { EntitlementStatusSchema } from "./entitlements";

/** One of the suite's licensed modules or features in this tenant: what applies now, why, and the dates. */
export const SuiteEntitlementSchema = z.object({
  code: z.string(),
  module: z.string(),
  description: z.string().nullable(),
  status: EntitlementStatusSchema,
  reason: z.string(),
  starts_on: z.string().nullable(),
  ends_on: z.string().nullable(),
  grace_ends_on: z.string().nullable(),
});
export type SuiteEntitlement = z.infer<typeof SuiteEntitlementSchema>;

/** When the suite was first installed in the tenant, and when an update last migrated and seeded it, each with its job and release. */
export const SuiteInstallationSchema = z.object({
  installed_at: z.string(),
  installed_by_job: z.string().nullable(),
  installed_version: z.string(),
  seeded_at: z.string(),
  seeded_by_job: z.string().nullable(),
  seeded_version: z.string(),
});
export type SuiteInstallation = z.infer<typeof SuiteInstallationSchema>;

/**
 * GET /api/v1/suites/procurement/about (TF-244, ADR-0059): what of the eProcurement Suite the tenant has, the release
 * the unit runs, the installation, the plan the tenant holds for the suite with its grace days, and the suite's modules
 * and features with their entitlement. Read only.
 */
export const SuiteAboutSchema = z.object({
  suite: z.string(),
  name: z.string(),
  description: z.string(),
  version: z.string(),
  installation: SuiteInstallationSchema.nullable(),
  plan: z.string().nullable(),
  grace_days: z.number(),
  stale: z.boolean(),
  modules: z.array(SuiteEntitlementSchema),
  features: z.array(SuiteEntitlementSchema),
});
export type SuiteAbout = z.infer<typeof SuiteAboutSchema>;

export const fetchProcurementAbout = (signal?: AbortSignal) =>
  request("/api/v1/suites/procurement/about", { schema: SuiteAboutSchema, ...(signal ? { signal } : {}) });
