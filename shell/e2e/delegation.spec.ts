import { expect, test, type Locator, type Page } from "@playwright/test";
import { call, loginId, open, signIn } from "./helpers";

/**
 * Delegation and access reviews against a real unit (A7, ADR-0037). `make e2e` sets up the data first
 * (infra/compose/scripts/e2e-data.py): the child-unit user, a colleague beside them, a role that reviews a unit's
 * access, and a role to grant and to revoke. The child-unit user reviews their unit only while this test runs, so the
 * other tests see a person with no console page. The administrator delegates user administration to the child-unit user, who acts for
 * them, sees their users, and grants the role, which the audit records with both names. Then a campaign on the child
 * unit, decided by its reviewer: a revoke ends the role at once. The delegation is ended and the campaign closed at the
 * end, whatever happens, so the next run starts clean.
 */
const child = { loginId: process.env.E2E_CHILD_LOGIN_ID ?? "", password: process.env.E2E_CHILD_PASSWORD ?? "" };
const administratorId = process.env.E2E_OUTSIDE_USER_ID ?? "";
const childId = process.env.E2E_CHILD_ID ?? "";
const childUnitId = process.env.E2E_CHILD_UNIT_ID ?? "";
const colleagueId = process.env.E2E_COLLEAGUE_ID ?? "";
const revocableRoleId = process.env.E2E_REVOCABLE_ROLE_ID ?? "";
const reviewerRoleId = process.env.E2E_REVIEWER_ROLE_ID ?? "";
const run = Date.now().toString(36);

interface Delegation {
  id: string;
  delegator: { id: string };
  delegate: { id: string };
  status: string;
}

/** Ends what the administrator delegates to the child-unit user, so a run never meets the last one's overlap. */
async function endDelegations(page: Page): Promise<void> {
  const mine = await call<{ data: Delegation[] }>(page, "GET", "/api/v1/delegations/mine");
  for (const delegation of mine.body.data) {
    if (delegation.delegator.id === administratorId && delegation.delegate.id === childId && ["scheduled", "active"].includes(delegation.status)) {
      await call(page, "POST", `/api/v1/delegations/${delegation.id}/end`, { reason: `End of e2e run ${run}` });
    }
  }
}

/** A person's assignments of one role. */
async function assignmentsOf(page: Page, principal: string, role: string): Promise<{ id: string }[]> {
  const assignments = await call<{ data: { id: string; role_id: string }[] }>(page, "GET", `/api/v1/role-assignments?filter=principal_id:eq:${principal}&page_size=100`);
  return assignments.body.data.filter((assignment) => assignment.role_id === role);
}

const revocableAssignments = (page: Page) => assignmentsOf(page, colleagueId, revocableRoleId);

/** A day as the date input takes it, in this machine's time zone, as the browser's. */
function day(offset: number): string {
  const date = new Date(Date.now() + offset * 24 * 60 * 60_000);
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

const loginCells = (grid: Locator) => grid.locator('[role="gridcell"][col-id="login_id"]');

test.describe("delegation and access reviews", () => {
  test.skip(child.loginId === "" || colleagueId === "" || revocableRoleId === "" || reviewerRoleId === "", "make e2e sets up the child unit's data first");

  test("the administrator delegates user administration, the delegate acts for them with both names in the audit, and a review on the child unit revokes a role at once", async ({ page, browser }, testInfo) => {
    test.setTimeout(180_000);
    await signIn(page);
    const administrator = (await call<{ display_name: string }>(page, "GET", "/api/v1/me")).body.display_name;
    await endDelegations(page);
    for (const leftover of await revocableAssignments(page)) {
      await call(page, "DELETE", `/api/v1/role-assignments/${leftover.id}`);
    }
    const reviews = await call(page, "POST", "/api/v1/role-assignments", { principal_id: childId, role_id: reviewerRoleId, scope_kind: "unit_with_children", unit_id: childUnitId });
    expect([200, 201]).toContain(reviews.status);

    const childContext = await browser.newContext({ baseURL: testInfo.project.use.baseURL, ignoreHTTPSErrors: true });
    const childPage = await childContext.newPage();
    let reviewId: string | undefined;
    try {
      // The administrator delegates in My profile: the person by their login, the duty in words, a reason.
      await open(page, "/me");
      await page.getByRole("tab", { name: "Delegations" }).click();
      await page.getByRole("button", { name: "Delegate your duties" }).click();
      const dialog = page.getByRole("dialog", { name: "Delegate your duties" });
      await dialog.getByRole("combobox", { name: "To whom" }).fill(child.loginId);
      await page.getByRole("option", { name: /^Child administrator/ }).click();
      await dialog.getByRole("checkbox", { name: "user administration" }).check();
      await dialog.getByRole("textbox", { name: "Reason" }).fill(`Annual leave, e2e run ${run}.`);
      await dialog.getByRole("button", { name: "Delegate" }).click();
      await expect(dialog).toBeHidden();
      await expect(page.getByRole("list", { name: "Your delegations" })).toContainText("You hand user administration to Child administrator");
      await page.screenshot({ path: "test-results/e2e/delegation-created.png", fullPage: true });

      // The delegate acts for the administrator from the header, and the band says whose rights, until when.
      await signIn(childPage, child);
      await childPage.getByRole("button", { name: "Act for someone who delegated duties to you" }).click();
      await childPage.getByRole("menuitem", { name: new RegExp(administrator) }).click();
      const band = childPage.getByRole("status").filter({ hasText: `You are acting for ${administrator}` });
      await expect(band).toBeVisible();
      await expect(band).toContainText("user administration");

      // With the administrator's rights for user administration: their users, whom the child unit alone does not hold.
      await open(childPage, "/users");
      const grid = childPage.getByRole("region", { name: "Users" });
      await expect(loginCells(grid).filter({ hasText: loginId })).toBeVisible();
      await childPage.screenshot({ path: "test-results/e2e/delegation-acting-users.png" });

      // A change made while acting names both people in the audit.
      const granted = await call<{ id: string }>(childPage, "POST", "/api/v1/role-assignments", { principal_id: colleagueId, role_id: revocableRoleId, scope_kind: "unit", unit_id: childUnitId });
      expect(granted.status).toBe(201);
      const events = await call<{ data: { event_type: string; record_id: string; actor_name: string; on_behalf_of_name: string }[] }>(
        page,
        "GET",
        `/api/v1/audit-events?filter=on_behalf_of_id:eq:${administratorId}&filter=event_type:eq:authz.role_assigned&page_size=20`,
      );
      const assigned = events.body.data.find((event) => event.record_id === granted.body.id);
      expect(assigned).toMatchObject({ actor_name: "Child administrator", on_behalf_of_name: administrator });

      await band.getByRole("button", { name: "Act as myself" }).click();
      await expect(band).toBeHidden();

      // The administrator starts a campaign on the child unit and the units below it.
      await open(page, "/console/access-reviews");
      await page.getByRole("button", { name: "Start a review" }).click();
      const start = page.getByRole("dialog", { name: "Start an access review" });
      await start.getByRole("textbox", { name: "Name" }).fill(`E2E review ${run}`);
      await start.getByLabel("Due").fill(day(7));
      await start.getByRole("tree").getByText(process.env.E2E_CHILD_UNIT ?? "E2E branch", { exact: true }).click();
      await start.getByRole("button", { name: "Start the review" }).click();
      await expect(page.getByRole("heading", { name: `E2E review ${run}` })).toBeVisible();
      reviewId = page.url().split("/").pop();

      // Its reviewer, the child-unit user, revokes the colleague's role; the revoke takes effect at once.
      await open(childPage, `/console/access-reviews/${reviewId}`);
      const row = childPage.getByRole("gridcell", { name: "Revocable in review" });
      await expect(row).toBeVisible();
      await childPage.screenshot({ path: "test-results/e2e/review-reviewer.png", fullPage: true });
      await row.click();
      await childPage.getByRole("button", { name: "Revoke", exact: true }).click();
      const revoke = childPage.getByRole("dialog", { name: "Revoke this role" });
      await revoke.getByRole("textbox", { name: "Comment" }).fill("Granted for the e2e run only.");
      await revoke.getByRole("button", { name: "Revoke now" }).click();
      await expect(childPage.getByText("Revoked: A colleague no longer holds Revocable in review.")).toBeVisible();
      expect(await revocableAssignments(page)).toEqual([]);

      // The reviewer's own roles in the unit are theirs to read, not to decide: the row says so, and the page has
      // nothing else left for them to confirm.
      await expect(childPage.getByRole("gridcell", { name: "About your own access: another reviewer decides it." }).first()).toBeVisible();
      await expect(childPage.getByRole("button", { name: "Confirm the 0 items on this page" })).toBeDisabled();
      await childPage.screenshot({ path: "test-results/e2e/review-own-rows.png", fullPage: true });

      // The administrator, who reviews every unit, confirms them, behind the page's confirmation.
      await open(page, `/console/access-reviews/${reviewId}`);
      const confirmPage = page.getByRole("button", { name: /^Confirm the [1-9]\d* items? on this page$/ });
      await expect(confirmPage).toBeEnabled();
      await confirmPage.click();
      const confirmation = page.getByRole("dialog", { name: "Confirm this page" });
      await confirmation.getByRole("button", { name: /^Confirm \d+ items?$/ }).click();
      await expect(page.getByText(/^Confirmed \d+ items?\.$/)).toBeVisible();
      await expect(confirmation).toBeHidden();
      await page.screenshot({ path: "test-results/e2e/review-decided.png", fullPage: true });
    } finally {
      await endDelegations(page);
      for (const reviewing of await assignmentsOf(page, childId, reviewerRoleId)) {
        await call(page, "DELETE", `/api/v1/role-assignments/${reviewing.id}`);
      }
      if (reviewId) {
        await call(page, "POST", `/api/v1/access-reviews/${reviewId}/close`);
      }
      await childContext.close();
    }
  });
});
