import { expect, test } from "@playwright/test";
import { call, open, signIn } from "./helpers";

interface DomainEvent {
  id: string;
  type: string;
  status: string;
  deliveries: { subscriber: string; status: string }[];
}

interface Listed<T> {
  data: T[];
}

/**
 * Domain events on a real unit (TF-085, TF-112, ADR-0060): a sample record created through the API raises its event in
 * the same transaction, the unit's worker hands it to the sample module's subscriber, which marks the record noticed,
 * and the console's Domain events page shows the event handled; a replay to the subscriber that handled it changes
 * nothing, says so, and is on the audit trail. Sample records exist only in a unit the operator marked for testing, as
 * the release's tenant and echo are.
 */
const run = process.env.E2E_RUN ?? `${Date.now()}`;
const runUnitId = process.env.E2E_RUN_UNIT_ID ?? "";
const subscriber = "samples.notice_created";

test.describe("the tenant's domain events", () => {
  test.skip(runUnitId === "", "make e2e sets up the run's unit first");

  test("a record's event is handled by its subscriber within seconds, and a replay to it from the console changes nothing and is audited [TF-085 AC1, TF-112 AC1, ADR-0060]", async ({
    page,
  }) => {
    test.setTimeout(120_000);
    await page.emulateMedia({ reducedMotion: "reduce" });
    await signIn(page);
    const probe = await call<unknown>(page, "GET", "/api/v1/sample-records?page_size=1");
    test.skip(
      probe.status === 404,
      "the unit does not run the sample module: make sample-records TENANT=x, then make update-unit",
    );

    const created = await call<{ id: string }>(page, "POST", "/api/v1/sample-records", {
      name: `Evented ${run}`,
      unit_id: runUnitId,
    });
    expect(created.status).toBe(201);
    const filter = encodeURIComponent(`record_id:eq:${created.body.id}`);
    let event: DomainEvent | undefined;
    await expect
      .poll(
        async () => {
          const answer = await call<Listed<DomainEvent>>(page, "GET", `/api/v1/events?filter=${filter}`);
          event = answer.body.data.find((e) => e.type === "samples.record.created");
          return event?.status ?? "";
        },
        { timeout: 10_000, intervals: [250] },
      )
      .toBe("handled");
    const record = await call<{ noticed_at: string | null }>(page, "GET", `/api/v1/sample-records/${created.body.id}`);
    expect(record.body.noticed_at, "the subscriber marked the record noticed").not.toBeNull();

    await open(page, `/console/events?event=${event!.id}`);
    await expect(page.getByRole("heading", { level: 2, name: "Domain events" })).toBeVisible();
    const detail = page.getByRole("region", { name: "samples.record.created (version 1)" });
    await expect(detail).toContainText(created.body.id);
    await expect(detail.getByRole("row").filter({ hasText: subscriber })).toContainText("Handled");
    await page.screenshot({ path: "test-results/e2e/events.png", fullPage: true });

    await detail.getByRole("button", { name: `Replay to ${subscriber}` }).click();
    const dialog = page.getByRole("dialog", { name: "Replay the event?" });
    await dialog.getByLabel(/^Reason/).fill(`End-to-end run ${run}: a replay of a handled event changes nothing.`);
    await dialog.getByRole("button", { name: "Replay" }).click();
    await expect(
      page.getByText(
        `1 event went to ${subscriber}: 0 to be tried again, 0 given for the first time, 1 already handled.`,
      ),
    ).toBeVisible();

    const audited = await call<Listed<{ reason: string | null }>>(
      page,
      "GET",
      `/api/v1/audit-events?filter=${encodeURIComponent("event_type:eq:events.replay_requested")}&filter=${encodeURIComponent(`record_id:eq:${event!.id}`)}`,
    );
    expect(audited.body.data).toHaveLength(1);
    expect(audited.body.data[0]?.reason).toContain(run);
  });
});
