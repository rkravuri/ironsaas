import { z } from "zod";
import { listSchema, request } from "./client";

/**
 * A security alert (TF-079, ADR-0056): the rule that raised it, its group (a person, an address, a target, a record, or
 * the whole tenant), what it counted within which window, the links to its events and its runbook, and its state.
 */
export const SecurityAlertSchema = z.object({
  id: z.string(),
  tenant: z.string(),
  origin: z.string(),
  rule: z.string(),
  title: z.string(),
  kind: z.enum(["threshold", "failing"]),
  severity: z.enum(["critical", "high", "normal"]),
  state: z.enum(["open", "acknowledged", "resolved"]),
  group_kind: z.string(),
  group_value: z.string(),
  group_name: z.string().nullable(),
  count: z.number(),
  threshold: z.number(),
  window_minutes: z.number(),
  first_event_at: z.string().nullable(),
  last_event_at: z.string().nullable(),
  raised_at: z.string(),
  reason: z.string().nullable(),
  link: z.string().nullable(),
  search: z.string().nullable(),
  runbook: z.string().nullable(),
  acknowledged_at: z.string().nullable(),
  acknowledged_by: z.string().nullable(),
  resolved_at: z.string().nullable(),
  resolved_by: z.string().nullable(),
  note: z.string().nullable(),
});
export type SecurityAlert = z.infer<typeof SecurityAlertSchema>;

export const SecurityAlertListSchema = listSchema(SecurityAlertSchema);

export const fetchSecurityAlertPage = (query: string, signal?: AbortSignal) =>
  request(`/api/v1/alerts${query ? `?${query}` : ""}`, {
    schema: SecurityAlertListSchema,
    ...(signal ? { signal } : {}),
  });

export const fetchSecurityAlert = (id: string, signal?: AbortSignal) =>
  request(`/api/v1/alerts/${encodeURIComponent(id)}`, { schema: SecurityAlertSchema, ...(signal ? { signal } : {}) });

export const acknowledgeSecurityAlert = (id: string, note: string | null) =>
  request(`/api/v1/alerts/${encodeURIComponent(id)}/acknowledge`, {
    method: "POST",
    body: { note },
    schema: SecurityAlertSchema,
  });

export const resolveSecurityAlert = (id: string, note: string) =>
  request(`/api/v1/alerts/${encodeURIComponent(id)}/resolve`, {
    method: "POST",
    body: { note },
    schema: SecurityAlertSchema,
  });

/** A rule's values at one level: the operator's, or what runs. */
export const RuleValuesSchema = z.object({
  threshold: z.number(),
  window_minutes: z.number(),
  cooldown_minutes: z.number(),
  above: z.number().nullable(),
  severity: z.string(),
});
export type RuleValues = z.infer<typeof RuleValuesSchema>;

/** What a tenant may set, always stricter than the operator's values. */
export const RuleBoundsSchema = z.object({
  threshold_editable: z.boolean(),
  threshold_minimum: z.number(),
  threshold_maximum: z.number(),
  window_minimum: z.number(),
  window_maximum: z.number(),
  above_editable: z.boolean(),
  above_minimum: z.number().nullable(),
  above_maximum: z.number().nullable(),
});
export type RuleBounds = z.infer<typeof RuleBoundsSchema>;

export const AlertRuleSchema = z.object({
  name: z.string(),
  title: z.string(),
  event_types: z.array(z.string()),
  group_by: z.string(),
  at_once: z.boolean(),
  runbook: z.string(),
  operator: RuleValuesSchema.nullable(),
  tenant: z.object({
    threshold: z.number().nullable(),
    window_minutes: z.number().nullable(),
    above: z.number().nullable(),
  }),
  effective: RuleValuesSchema.nullable(),
  bounds: RuleBoundsSchema.nullable(),
  state: z.enum(["running", "failing"]),
  reason: z.string().nullable(),
  last_run_at: z.string().nullable(),
  failing_since: z.string().nullable(),
});
export type AlertRule = z.infer<typeof AlertRuleSchema>;

export const AlertRulesSchema = z.object({ data: z.array(AlertRuleSchema), evaluated_at: z.string().nullable() });
export type AlertRules = z.infer<typeof AlertRulesSchema>;

export const fetchAlertRules = (signal?: AbortSignal) =>
  request("/api/v1/alert-rules", { schema: AlertRulesSchema, ...(signal ? { signal } : {}) });

/** A tenant's values for a rule; a null returns that value to the operator's. */
export interface AlertRuleValues {
  threshold: number | null;
  window_minutes: number | null;
  above: number | null;
}

export const setAlertRule = (name: string, values: AlertRuleValues) =>
  request(`/api/v1/alert-rules/${encodeURIComponent(name)}`, {
    method: "PUT",
    body: values,
    schema: AlertRuleSchema,
  });
