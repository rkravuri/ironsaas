import type { TFunction } from "i18next";
import type { Job, JobRun } from "@/api/jobs";

export const readJobs = "jobs.jobs.read";
export const manageJobs = "jobs.jobs.manage";

/**
 * A job's schedule in the person's words. The API says it as the catalogue does (<c>every 5m</c>, <c>cron 0 2 * * *</c>,
 * <c>when started</c>); the interval and the expression stay as they are, the words around them are translated.
 */
export function scheduleWords(schedule: string, t: TFunction): string {
  if (schedule.startsWith("every ")) return t("jobs.schedule.every", { interval: schedule.slice("every ".length) });
  if (schedule.startsWith("cron ")) return t("jobs.schedule.cron", { expression: schedule.slice("cron ".length) });
  return t("jobs.schedule.whenStarted");
}

/** How long a run took: milliseconds below a second, seconds with one decimal below a minute, then minutes. */
export function durationWords(ms: number | null, t: TFunction): string {
  if (ms == null) return "";
  if (ms < 1000) return t("jobs.duration.ms", { count: ms });
  if (ms < 60_000) return t("jobs.duration.seconds", { value: (ms / 1000).toFixed(1) });
  return t("jobs.duration.minutes", { value: (ms / 60_000).toFixed(1) });
}

/** Whether a person may pause, resume or start the job here: a job each process runs, or a held one, has no run to act on. */
export function actsOnRuns(job: Job): boolean {
  return job.scope === "unit" && job.state !== "held";
}

/** A run that may run again: the console runs a dead letter again, after its cause is fixed. */
export function rerunnable(run: JobRun): boolean {
  return run.status === "dead";
}
