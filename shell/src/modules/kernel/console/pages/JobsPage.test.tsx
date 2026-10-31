import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fill, json, me, renderWithProviders, stubFetch } from "@/test/render";
import { DeadLettersPage } from "./DeadLettersPage";
import { durationWords, scheduleWords } from "./job-words";
import { JobsPage } from "./JobsPage";

afterEach(() => {
  vi.unstubAllGlobals();
});

const list = (data: unknown[]) => ({
  data,
  meta: {
    page_number: 1,
    page_size: 50,
    total_count: data.length,
    total_pages: 1,
    has_previous: false,
    has_next: false,
    page_size_capped: false,
    max_page_size: 200,
  },
  links: { self: "x", first: "x", previous: null, next: null, last: "x" },
});

const failedRun = {
  id: "0198f0c2-0000-7000-8000-0000000000f1",
  job: "audit.retention",
  key: "2026-10-31T00:10:00.0000000+00:00",
  status: "dead",
  priority: "bulk",
  attempts: 5,
  scheduled_for: "2026-10-31T00:10:00Z",
  due_at: "2026-10-31T00:10:00Z",
  started_at: "2026-10-31T00:12:30Z",
  finished_at: "2026-10-31T00:12:31Z",
  duration_ms: 812,
  error: "audit.archive.unavailable: The storage did not answer.",
  triggered_by: null,
  reason: null,
  dead_at: "2026-10-31T00:12:31Z",
  reruns: 0,
  log: "Archiving 120 events of class standard.\nThe storage did not answer.",
};

const retention = {
  name: "audit.retention",
  module: "audit",
  suite: null,
  description: "Archives and removes audit events past their retention.",
  owner: "Platform security",
  schedule: "every 1d",
  scope: "unit",
  priority: "bulk",
  timeout_seconds: 7200,
  attempts: 5,
  max_parallel: 1,
  unique_by: "the time it was scheduled for",
  state: "active",
  paused_at: null,
  paused_by: null,
  pause_reason: null,
  next_run_at: "2026-11-01T00:10:00Z",
  last_run: failedRun,
  last_failure: failedRun,
  typical_duration_ms: 900,
  queued: 0,
  running: 0,
  dead_letters: 1,
};

const refresh = {
  ...retention,
  name: "flags.refresh_cache",
  module: "flags",
  description: "Reads the flag rules into this process.",
  schedule: "every 30s",
  scope: "each_process",
  priority: "normal",
  state: "each_process",
  next_run_at: null,
  last_run: null,
  last_failure: null,
  typical_duration_ms: null,
  dead_letters: 0,
};

const handlers = (permissions: string[], extra: Record<string, (init: RequestInit | undefined) => Response> = {}) => ({
  "GET /api/v1/me": () => json(me(permissions)),
  "GET /api/v1/jobs": () => json([retention, refresh]),
  "GET /api/v1/jobs/audit.retention": () => json(retention),
  "GET /api/v1/jobs/audit.retention/runs?page_number=1&page_size=50&sort=-scheduled_for": () => json(list([failedRun])),
  ...extra,
});

describe("the console's jobs page", () => {
  it("lists every job with its schedule, class, state, last run and backlog in words, and offers no action to a reader [TF-121 AC1, ADR-0061, ADR-0024]", async () => {
    stubFetch(handlers(["jobs.jobs.read"]));
    renderWithProviders(<JobsPage />, { route: "/console/jobs" });

    expect(await screen.findByRole("heading", { level: 2, name: "Jobs" })).toBeInTheDocument();
    const table = await screen.findByRole("table", { name: /The jobs of your organisation/ });
    const row = within(table).getByText("audit.retention").closest("tr") as HTMLElement;
    expect(row).toHaveTextContent("Every 1d, bulk work");
    expect(row).toHaveTextContent("Active");
    expect(row).toHaveTextContent("Dead letter");
    expect(row).toHaveTextContent("812 milliseconds");
    expect(row).toHaveTextContent("1 dead letter");
    const process = within(table).getByText("flags.refresh_cache").closest("tr") as HTMLElement;
    expect(process).toHaveTextContent("In each process");
    expect(process).toHaveTextContent("Not run yet");
    expect(screen.queryByRole("button", { name: "Pause" })).not.toBeInTheDocument();
  });

  it("opens a failed job with its error and its run log [TF-121 AC1, ADR-0061]", async () => {
    stubFetch(handlers(["jobs.jobs.read"]));
    renderWithProviders(<JobsPage />, { route: "/console/jobs?job=audit.retention" });

    const failure = await screen.findByText(/Last failure/);
    const alert = failure.closest("[data-job-failure]") as HTMLElement;
    expect(alert).toHaveTextContent("audit.archive.unavailable: The storage did not answer.");
    expect(alert).toHaveTextContent("Archiving 120 events of class standard.");
    expect(screen.getByRole("heading", { level: 3, name: "audit.retention" })).toBeInTheDocument();
    expect(screen.getByText("Platform security")).toBeInTheDocument();
  });

  it("pauses a job only with a reason, then shows it paused with why [TF-121 AC2, ADR-0061]", async () => {
    const paused = {
      ...retention,
      state: "paused",
      paused_at: "2026-10-31T10:00:00Z",
      pause_reason: "The archive is being moved.",
    };
    let pausedNow = false;
    const { calls } = stubFetch(
      handlers(["jobs.jobs.read", "jobs.jobs.manage"], {
        "POST /api/v1/jobs/audit.retention/pause": () => {
          pausedNow = true;
          return json(paused);
        },
        "GET /api/v1/jobs/audit.retention": () => json(pausedNow ? paused : retention),
      }),
    );
    renderWithProviders(<JobsPage />, { route: "/console/jobs?job=audit.retention" });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Pause" }));
    const dialog = await screen.findByRole("dialog", { name: "Pause audit.retention?" });
    await user.click(within(dialog).getByRole("button", { name: "Pause" }));
    expect(calls.some((call) => call.key === "POST /api/v1/jobs/audit.retention/pause")).toBe(false);
    await fill(user, within(dialog).getByLabelText(/^Reason/), "The archive is being moved.");
    await user.click(within(dialog).getByRole("button", { name: "Pause" }));

    await waitFor(() =>
      expect(calls.some((call) => call.key === "POST /api/v1/jobs/audit.retention/pause")).toBe(true),
    );
    const pause = calls.find((call) => call.key === "POST /api/v1/jobs/audit.retention/pause");
    expect(JSON.parse(pause?.init?.body as string)).toEqual({ reason: "The archive is being moved." });
    expect(
      await screen.findByText("audit.retention is paused. Resume it when its work may go on."),
    ).toBeInTheDocument();
    expect(await screen.findByText(/Paused since .*: The archive is being moved\./)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Resume" })).toBeInTheDocument();
  });

  it("says a schedule and a duration in words [ADR-0061]", () => {
    const t = ((key: string, values?: Record<string, unknown>) => `${key} ${JSON.stringify(values ?? {})}`) as never;
    expect(scheduleWords("every 5m", t)).toBe('jobs.schedule.every {"interval":"5m"}');
    expect(scheduleWords("cron 0 2 * * *", t)).toBe('jobs.schedule.cron {"expression":"0 2 * * *"}');
    expect(scheduleWords("when started", t)).toBe("jobs.schedule.whenStarted {}");
    expect(durationWords(null, t)).toBe("");
    expect(durationWords(1500, t)).toBe('jobs.duration.seconds {"count":1.5}');
    expect(durationWords(7_200_000, t)).toBe('jobs.duration.hours {"count":2}');
  });
});

describe("the console's dead letters page", () => {
  const letters = [
    {
      kind: "job",
      id: failedRun.id,
      name: "audit.retention",
      what: failedRun.key,
      attempts: 5,
      error: failedRun.error,
      dead_at: "2026-10-31T00:12:31Z",
    },
    {
      kind: "event",
      id: "0198f0c2-0000-7000-8000-0000000000e1",
      name: "authz.end_delegations_of_deactivated",
      what: "identity.person.deactivated (version 1)",
      attempts: 6,
      error: "authz.delegation.locked: The delegation is being changed.",
      dead_at: "2026-10-31T09:20:00Z",
    },
  ];
  const deadHandlers = (
    permissions: string[],
    extra: Record<string, (init: RequestInit | undefined) => Response> = {},
  ) => ({
    "GET /api/v1/me": () => json(me(permissions)),
    "GET /api/v1/dead-letters?page_number=1&page_size=50&sort=-dead_at": () => json(list(letters)),
    ...extra,
  });

  it("runs a job's dead letter again with a reason, in the one view of jobs and events [ADR-0061]", async () => {
    const { calls } = stubFetch(
      deadHandlers(["jobs.jobs.read", "jobs.jobs.manage"], {
        [`POST /api/v1/jobs/runs/${failedRun.id}/rerun`]: () =>
          json({ ...failedRun, status: "queued", attempts: 0, reruns: 1 }),
      }),
    );
    renderWithProviders(<DeadLettersPage />, { route: "/console/dead-letters" });
    const user = userEvent.setup();

    const grid = await screen.findByRole("region", { name: "Dead letters of jobs and events" }, { timeout: 10_000 });
    expect(await within(grid).findByText("Job run")).toBeInTheDocument();
    expect(within(grid).getByText("Event")).toBeInTheDocument();
    await user.click(within(grid).getByText("audit.retention"));
    const detail = await screen.findByRole("region", { name: "audit.retention" });
    expect(detail).toHaveTextContent("failed 5 times");
    await user.click(within(detail).getByRole("button", { name: "Run again" }));
    const dialog = await screen.findByRole("dialog", { name: "Run the job's run again?" });
    await fill(user, within(dialog).getByLabelText(/^Reason/), "The storage answers again.");
    await user.click(within(dialog).getByRole("button", { name: "Run again" }));

    await waitFor(() =>
      expect(calls.some((call) => call.key === `POST /api/v1/jobs/runs/${failedRun.id}/rerun`)).toBe(true),
    );
    expect(await screen.findByText("The run of audit.retention is waiting for the worker again.")).toBeInTheDocument();
  });

  it("offers an event's dead letter no replay to a person who may not replay events [ADR-0061, ADR-0060]", async () => {
    stubFetch(deadHandlers(["jobs.jobs.read", "jobs.jobs.manage"]));
    renderWithProviders(<DeadLettersPage />, { route: "/console/dead-letters" });
    const user = userEvent.setup();

    const grid = await screen.findByRole("region", { name: "Dead letters of jobs and events" }, { timeout: 10_000 });
    await user.click(await within(grid).findByText("authz.end_delegations_of_deactivated"));
    const detail = await screen.findByRole("region", { name: "authz.end_delegations_of_deactivated" });
    expect(within(detail).queryByRole("button", { name: "Replay" })).not.toBeInTheDocument();
    expect(within(detail).getByRole("link", { name: "Open the event" })).toHaveAttribute(
      "href",
      "/console/events?event=0198f0c2-0000-7000-8000-0000000000e1",
    );
  });
});
