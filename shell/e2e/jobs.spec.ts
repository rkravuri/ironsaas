import { expect, test } from "@playwright/test";
import { call, open, signIn } from "./helpers";

interface JobRun {
  id: string;
  status: string;
  log: string | null;
}

interface Listed<T> {
  data: T[];
}

/**
 * Jobs on a real unit (TF-120, TF-121, ADR-0061): the console's Jobs page lists the unit's jobs, the worker's
 * housekeeping among them; the administrator starts the sample module's job now with a reason, the unit's worker runs it,
 * the page shows its run succeeded with its log, and the start is on the audit trail with the reason. The sample job
 * exists only in a unit the operator marked for testing, as the release's tenant and echo are.
 */
const run = process.env.E2E_RUN ?? `${Date.now()}`;
const runUnitId = process.env.E2E_RUN_UNIT_ID ?? "";
const job = "samples.tally_records";

test.describe("the tenant's jobs", () => {
  test.skip(runUnitId === "", "make e2e sets up the run's unit first");

  test("the administrator starts a job now with a reason from the console, the worker runs it, and the start is audited [TF-121 AC2, ADR-0061]", async ({
    page,
  }) => {
    test.setTimeout(120_000);
    await page.emulateMedia({ reducedMotion: "reduce" });
    await signIn(page);
    const probe = await call<unknown>(page, "GET", `/api/v1/jobs/${job}`);
    test.skip(
      probe.status === 404,
      "the unit does not run the sample module: make sample-records TENANT=x, then make update-unit",
    );

    await open(page, `/console/jobs?job=${job}`);
    await expect(page.getByRole("heading", { level: 2, name: "Jobs" })).toBeVisible();
    const table = page.getByRole("table", { name: /The jobs of your organisation/ });
    await expect(table.getByRole("row").filter({ hasText: "audit.seal_chain" })).toContainText("Active");
    await expect(table.getByRole("row").filter({ hasText: "events.dispatch" })).toContainText("Interactive");

    await page.getByRole("button", { name: "Run now" }).click();
    const dialog = page.getByRole("dialog", { name: `Run ${job} now?` });
    await dialog.getByLabel(/^Reason/).fill(`End-to-end run ${run}: the console starts a job.`);
    await dialog.getByRole("button", { name: "Run now" }).click();
    await expect(page.getByText(`A run of ${job} is waiting for the worker. Its runs show how it went.`)).toBeVisible();

    let started: JobRun | undefined;
    await expect
      .poll(
        async () => {
          const answer = await call<Listed<JobRun>>(page, "GET", `/api/v1/jobs/${job}/runs`);
          started = answer.body.data[0];
          return started?.status ?? "";
        },
        { timeout: 30_000, intervals: [500] },
      )
      .toBe("succeeded");

    await open(page, `/console/jobs?job=${job}`);
    const runs = page.getByRole("grid", { name: `Runs of ${job}` });
    await runs.getByText("Succeeded").first().click();
    await expect(page.locator("[data-run-detail]")).toContainText("sample record(s)");
    await page.screenshot({ path: "test-results/e2e/jobs.png", fullPage: true });

    const audited = await call<Listed<{ reason: string | null }>>(
      page,
      "GET",
      `/api/v1/audit-events?filter=${encodeURIComponent("event_type:eq:jobs.run_triggered")}&filter=${encodeURIComponent(`record_id:eq:${started!.id}`)}`,
    );
    expect(audited.body.data).toHaveLength(1);
    expect(audited.body.data[0]?.reason).toContain(run);
  });
});
