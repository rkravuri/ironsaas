import { readFile } from "node:fs/promises";
import { expect, test, type Browser, type Page } from "@playwright/test";
import { call, open, signIn } from "./helpers";

/**
 * Evidence, four-eyes approval and validation against a real unit (S3 T8, TF-067, TF-068, TF-218, ADR-0052). The
 * administrator makes an evidence pack for the last month and downloads it, both audited with their name, while a
 * person without the permission is refused. With four-eyes asked for roles, the administrator's new role waits; they
 * cannot approve it themselves, and the run's second administrator approves it in the console, so it applies with both
 * identities in the audit trail. Four-eyes is set back, approved by the second administrator too, whatever happens. Then
 * the administrator downloads the validation pack of the version the unit runs.
 */
const run = Date.now().toString(36);
const approver = {
  id: process.env.E2E_APPROVER_ID ?? "",
  loginId: process.env.E2E_APPROVER_LOGIN_ID ?? "",
  password: process.env.E2E_APPROVER_PASSWORD ?? "",
};
const child = { loginId: process.env.E2E_CHILD_LOGIN_ID ?? "", password: process.env.E2E_CHILD_PASSWORD ?? "" };
const fourEyesAreas = "changes.four_eyes.areas";

/** A UTC calendar day, offset from today. */
const day = (offset: number) => new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);

interface AuditRow {
  event_type: string;
  actor_id: string | null;
  record_id: string | null;
  details: Record<string, string> | null;
}

/** The newest audit events of a type, as the trail lists them. */
async function eventsOf(page: Page, type: string): Promise<AuditRow[]> {
  const found = await call<{ data: AuditRow[] }>(
    page,
    "GET",
    `/api/v1/audit-events?filter=event_type:eq:${type}&sort=-created_at&page_size=20`,
  );
  expect(found.status).toBe(200);
  return found.body.data;
}

/** A file the browser saved, with the names a ZIP keeps in plain text: its entries. */
async function savedZip(page: Page, click: () => Promise<void>): Promise<string> {
  const download = page.waitForEvent("download");
  await click();
  const saved = await download;
  const bytes = await readFile(await saved.path());
  expect(bytes.subarray(0, 2).toString("latin1"), "a ZIP starts with PK").toBe("PK");
  return bytes.toString("latin1");
}

/** The second administrator, signed in with their password in a browser context of their own. */
async function secondAdministrator(browser: Browser): Promise<Page> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await signIn(page, { loginId: approver.loginId, password: approver.password });
  return page;
}

test.describe("evidence, four-eyes approval and validation", () => {
  test("the administrator makes an evidence pack for a period and downloads its five documents with the manifest, both audited with them as requester, and a person without the permission is refused [TF-067 AC1, TF-067 AC2, TF-067 AC3, ADR-0052]", async ({
    page,
    browser,
  }) => {
    test.setTimeout(120_000);
    await signIn(page);
    const me = await call<{ id: string }>(page, "GET", "/api/v1/me");

    await open(page, "/compliance/evidence-packs");
    await page.getByRole("button", { name: "Make a pack" }).click();
    const form = page.getByRole("dialog", { name: "Make an evidence pack" });
    await form.getByLabel("From", { exact: true }).fill(day(-30));
    await form.getByLabel("Up to and including", { exact: true }).fill(day(-1));
    await form.getByRole("button", { name: "Make the pack" }).click();
    await expect(page).toHaveURL(/\/compliance\/evidence-packs\/[0-9a-f-]{36}$/);
    const packId = page.url().split("/").at(-1) ?? "";
    // The worker makes the pack within seconds; the page reads it again until it is ready.
    await expect(page.getByText("Ready", { exact: true })).toBeVisible({ timeout: 60_000 });
    await expect(page.locator("[data-chain-head]")).toContainText("Segment");
    await page.screenshot({ path: "test-results/e2e/evidence-pack.png", fullPage: true });

    const zip = await savedZip(page, () => page.getByRole("button", { name: "Download" }).click());
    expect(zip).toContain("manifest.json");
    for (const folder of [
      "1-access_list/access_list.html",
      "2-administrative_actions/administrative_actions.html",
      "3-security_settings/security_settings.html",
      "4-chain_verification/chain_verification.html",
      "5-backup_drills/backup_drills.html",
    ]) {
      expect(zip, `the pack holds ${folder}`).toContain(folder);
    }

    const requested = (await eventsOf(page, "compliance.evidence_pack_requested")).find((e) => e.record_id === packId);
    expect(requested?.actor_id).toBe(me.body.id);
    await expect
      .poll(async () =>
        (await eventsOf(page, "compliance.evidence_pack_downloaded")).find((e) => e.record_id === packId),
      )
      .toMatchObject({ actor_id: me.body.id });

    const context = await browser.newContext();
    const outsider = await context.newPage();
    await signIn(outsider, child);
    const refused = await call<{ error_code: string }>(outsider, "POST", "/api/v1/evidence-packs", {
      from: `${day(-30)}T00:00:00Z`,
      to: `${day(-1)}T00:00:00Z`,
    });
    expect(refused.status).toBe(403);
    await context.close();
  });

  test("with four-eyes on roles, the administrator's new role waits, they cannot approve it, and a second administrator approves it so it applies with both identities audited [TF-068 AC1, TF-068 AC2, TF-068 AC3, ADR-0052]", async ({
    page,
    browser,
  }) => {
    test.setTimeout(120_000);
    expect(approver.password, "make e2e exports the second administrator").not.toBe("");
    await signIn(page);
    const second = await secondAdministrator(browser);
    const me = await call<{ id: string }>(page, "GET", "/api/v1/me");
    const code = `e2e-four-eyes-${run}`;
    let fourEyesOn = false;
    try {
      const on = await call(page, "PUT", `/api/v1/settings/${fourEyesAreas}`, {
        scope: "tenant",
        unit_id: null,
        value: ["roles"],
      });
      expect(on.status).toBe(200);
      fourEyesOn = true;

      const held = await call<{ error_code: string; change_id: string }>(page, "POST", "/api/v1/roles", {
        code,
        name: `Four-eyes role ${run}`,
        description: "Made by the browser test of four-eyes approval.",
        principal_type: "user",
        permissions: ["authz.roles.read"],
      });
      expect(held.status).toBe(202);
      expect(held.body.error_code).toBe("changes.change.held");
      const changeId = held.body.change_id;
      const missing = await call<{ data: unknown[] }>(page, "GET", `/api/v1/roles?filter=code:eq:${code}`);
      expect(missing.body.data, "nothing applies before the approval").toHaveLength(0);

      // The requester sees their change, with no way to approve it, and the API refuses it to them.
      await open(page, `/console/changes/${changeId}`);
      await expect(page.getByText(/You asked for this change, so someone else approves it/)).toBeVisible();
      await expect(page.getByRole("button", { name: "Approve" })).toHaveCount(0);
      const own = await call<{ error_code: string }>(page, "POST", `/api/v1/changes/${changeId}/approve`, {
        reason: "Approving my own change.",
      });
      expect(own.status).toBe(403);
      expect(own.body.error_code).toBe("changes.change.own");

      await open(second, "/console/changes");
      await second.getByRole("gridcell", { name: `Create the role Four-eyes role ${run}` }).dblclick();
      await expect(second.locator("[data-before-after]")).toContainText("authz.roles.read");
      await second.screenshot({ path: "test-results/e2e/change-to-approve.png", fullPage: true });
      await second.getByRole("button", { name: "Approve" }).click();
      const dialog = second.getByRole("dialog", { name: "Approve the change" });
      await dialog.getByLabel(/^Why you approve it/).fill(`Checked with the requester, e2e run ${run}.`);
      await dialog.getByRole("button", { name: "Approve" }).click();
      await expect(second.getByText("Approved and applied", { exact: true })).toBeVisible();
      await second.screenshot({ path: "test-results/e2e/change-approved.png", fullPage: true });

      const made = await call<{ data: { id: string }[] }>(page, "GET", `/api/v1/roles?filter=code:eq:${code}`);
      expect(made.body.data).toHaveLength(1);
      const approved = (await eventsOf(page, "changes.change_approved")).find((e) => e.record_id === changeId);
      expect(approved?.actor_id).toBe(approver.id);
      expect(approved?.details).toMatchObject({ requested_by: me.body.id, decided_by: approver.id });
    } finally {
      // A change of this run or an earlier one that still waits holds its subject: the administrator withdraws it.
      const waiting = await call<{ data: { id: string }[] }>(
        page,
        "GET",
        "/api/v1/changes?view=mine&status=pending&page_size=100",
      );
      for (const left of waiting.body.data ?? []) {
        expect((await call(page, "POST", `/api/v1/changes/${left.id}/withdraw`)).status).toBe(200);
      }
      if (fourEyesOn) {
        // Setting four-eyes back is itself a change that waits while an area is listed: the second administrator approves it.
        const reset = await call<{ change_id?: string }>(
          page,
          "DELETE",
          `/api/v1/settings/${fourEyesAreas}?scope=tenant`,
        );
        if (reset.status === 202 && reset.body.change_id) {
          const approvedReset = await call(second, "POST", `/api/v1/changes/${reset.body.change_id}/approve`, {
            reason: `Ends the four-eyes test, e2e run ${run}.`,
          });
          expect(approvedReset.status).toBe(200);
        } else {
          expect(reset.status).toBe(200);
        }
      }
      // The run's role, and any an earlier run left: with four-eyes off again, each goes at once.
      const left = await call<{ data: { id: string }[] }>(
        page,
        "GET",
        "/api/v1/roles?filter=code:startswith:e2e-four-eyes-&page_size=100",
      );
      for (const role of left.body.data ?? []) {
        expect((await call(page, "DELETE", `/api/v1/roles/${role.id}`)).status).toBe(204);
      }
      await second.context().close();
    }
  });

  test("the administrator downloads the validation pack of the version the unit runs from the console, audited [TF-218 AC3, ADR-0052]", async ({
    page,
  }) => {
    await signIn(page);
    const me = await call<{ id: string }>(page, "GET", "/api/v1/me");
    const validation = await call<{ version: string; development: boolean; pack_available: boolean }>(
      page,
      "GET",
      "/api/v1/validation",
    );
    expect(validation.status).toBe(200);
    // A release's own integration stage runs before its pack exists: the pack is made from that stage's results.
    test.skip(
      !validation.body.pack_available,
      "No validation pack of this version in the unit yet: make validation-pack, then make update-unit, places one.",
    );

    await open(page, "/compliance/validation");
    await expect(page.locator("[data-version]").first()).toHaveText(validation.body.version);
    await page.screenshot({ path: "test-results/e2e/validation.png", fullPage: true });
    const zip = await savedZip(page, () => page.getByRole("link", { name: "Download the pack" }).click());
    expect(zip).toContain("manifest.json");
    expect(zip).toContain("traceability");

    await expect
      .poll(async () => (await eventsOf(page, "compliance.validation_pack_downloaded"))[0]?.actor_id)
      .toBe(me.body.id);
  });
});
