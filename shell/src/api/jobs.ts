import { z } from "zod";
import { listSchema, request } from "./client";

/** The order a unit takes its work in (TF-122, ADR-0061): what a person waits for, housekeeping, then bulk work. */
export const PriorityClassSchema = z.enum(["interactive", "normal", "bulk"]);
export type PriorityClass = z.infer<typeof PriorityClassSchema>;

/**
 * One run of a job (TF-120, ADR-0061): its key, where it stands, its attempts, when it was due, started and finished, its
 * error and log, who started it and why, and how often it was run again.
 */
export const JobRunSchema = z.object({
  id: z.string(),
  job: z.string(),
  key: z.string(),
  status: z.enum(["queued", "running", "succeeded", "dead", "skipped", "held"]),
  priority: PriorityClassSchema,
  attempts: z.number(),
  scheduled_for: z.string(),
  due_at: z.string(),
  started_at: z.string().nullable(),
  finished_at: z.string().nullable(),
  duration_ms: z.number().nullable(),
  error: z.string().nullable(),
  triggered_by: z.string().nullable(),
  reason: z.string().nullable(),
  dead_at: z.string().nullable(),
  reruns: z.number(),
  log: z.string().nullable(),
});
export type JobRun = z.infer<typeof JobRunSchema>;

export const JobRunListSchema = listSchema(JobRunSchema);

/**
 * A job as the console lists it (TF-121, ADR-0061): what it does and who owns it, how it runs, its state, its next and
 * last runs, its last failure, how long it usually takes and its backlog.
 */
export const JobSchema = z.object({
  name: z.string(),
  module: z.string(),
  suite: z.string().nullable(),
  description: z.string(),
  owner: z.string(),
  schedule: z.string(),
  scope: z.enum(["unit", "each_process"]),
  priority: PriorityClassSchema,
  timeout_seconds: z.number(),
  attempts: z.number(),
  max_parallel: z.number(),
  unique_by: z.string(),
  state: z.enum(["active", "paused", "held", "each_process"]),
  paused_at: z.string().nullable(),
  paused_by: z.string().nullable(),
  pause_reason: z.string().nullable(),
  next_run_at: z.string().nullable(),
  last_run: JobRunSchema.nullable(),
  last_failure: JobRunSchema.nullable(),
  typical_duration_ms: z.number().nullable(),
  queued: z.number(),
  running: z.number(),
  dead_letters: z.number(),
});
export type Job = z.infer<typeof JobSchema>;

/** A dead letter of a job, run again by its run's id, or of an event subscription, replayed to its subscriber. */
export const AnyDeadLetterSchema = z.object({
  kind: z.enum(["job", "event"]),
  id: z.string(),
  name: z.string(),
  what: z.string(),
  attempts: z.number(),
  error: z.string().nullable(),
  dead_at: z.string(),
});
export type AnyDeadLetter = z.infer<typeof AnyDeadLetterSchema>;

export const AnyDeadLetterListSchema = listSchema(AnyDeadLetterSchema);

export const fetchJobs = (signal?: AbortSignal) =>
  request("/api/v1/jobs", { schema: z.array(JobSchema), ...(signal ? { signal } : {}) });

export const fetchJob = (name: string, signal?: AbortSignal) =>
  request(`/api/v1/jobs/${encodeURIComponent(name)}`, { schema: JobSchema, ...(signal ? { signal } : {}) });

export const fetchJobRunPage = (name: string, query: string, signal?: AbortSignal) =>
  request(`/api/v1/jobs/${encodeURIComponent(name)}/runs${query ? `?${query}` : ""}`, {
    schema: JobRunListSchema,
    ...(signal ? { signal } : {}),
  });

export const fetchJobRun = (id: string, signal?: AbortSignal) =>
  request(`/api/v1/jobs/runs/${encodeURIComponent(id)}`, { schema: JobRunSchema, ...(signal ? { signal } : {}) });

export const fetchAnyDeadLetterPage = (query: string, signal?: AbortSignal) =>
  request(`/api/v1/dead-letters${query ? `?${query}` : ""}`, {
    schema: AnyDeadLetterListSchema,
    ...(signal ? { signal } : {}),
  });

/** What a person does to a job: each with the reason the audit trail keeps, after a recent sign-in. */
export type JobAction = "pause" | "resume" | "run";

export const actOnJob = (name: string, action: JobAction, reason: string) =>
  action === "run"
    ? request(`/api/v1/jobs/${encodeURIComponent(name)}/run`, {
        method: "POST",
        body: { reason },
        schema: JobRunSchema,
      })
    : request(`/api/v1/jobs/${encodeURIComponent(name)}/${action}`, {
        method: "POST",
        body: { reason },
        schema: JobSchema,
      });

export const rerunJobRun = (id: string, reason: string) =>
  request(`/api/v1/jobs/runs/${encodeURIComponent(id)}/rerun`, {
    method: "POST",
    body: { reason },
    schema: JobRunSchema,
  });
