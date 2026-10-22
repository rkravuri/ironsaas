import { expect, test } from "@playwright/test";
import { call, open, signIn } from "./helpers";

/**
 * Impersonation on a real unit (S3 T5, TF-025, ADR-0049). `make e2e` makes a person in the run's unit; the administrator
 * signs in as them from their record with a reason, acts as them under the band, cannot confirm who they are, and ends it;
 * the audit trail names both on the action, and the person, signed in themselves, sees who did it, when and why.
 */
const person = {
  id: process.env.E2E_IMPERSONATED_ID ?? "",
  loginId: process.env.E2E_IMPERSONATED_LOGIN_ID ?? "",
  password: process.env.E2E_IMPERSONATED_PASSWORD ?? "",
};

test.describe("signing in as someone", () => {
  test.skip(person.id === "", "make e2e sets up the data first");

  test("an administrator signs in as a person with a reason under a band, every action names both, they never step up, and the person sees it [TF-025 AC1, TF-025 AC2, TF-025 AC5, TF-025 AC6, ADR-0049]", async ({
    page,
    browser,
  }, testInfo) => {
    test.setTimeout(120_000);
    const reason = `The browser test signs in as them (${Date.now()})`;
    await signIn(page);
    const administrator = await call<{ id: string; display_name: string }>(page, "GET", "/api/v1/me");

    await open(page, `/users/${person.id}`);
    await page.locator("[data-panel=record-header]").getByRole("button", { name: "Sign in as" }).click();
    const dialog = page.getByRole("dialog", { name: /^Sign in as .*\?$/ });
    await dialog.getByRole("button", { name: "Sign in as them" }).click();
    await expect(dialog.getByText("Say why: the reason is kept in the audit trail.")).toBeVisible();
    await dialog.getByLabel(/^Reason/).fill(reason);
    await dialog.getByRole("button", { name: "Sign in as them" }).click();

    // The whole shell starts again as the person, under the band, first thing on every page.
    const band = page.locator("[data-impersonation-band]");
    await expect(band).toContainText("You are signed in as Impersonated person");
    await expect(band).toContainText(`Reason: ${reason}`);
    await page.screenshot({ path: "test-results/e2e/impersonation-band.png" });
    const me = await call<{ id: string; home_unit: { id: string }; impersonation: { impersonator_name: string } }>(
      page,
      "GET",
      "/api/v1/me",
    );
    expect(me.body.id).toBe(person.id);
    expect(me.body.impersonation.impersonator_name).toBe(administrator.body.display_name);

    // An action as the person, and what an impersonator may never do.
    const chosen = await call(page, "POST", "/api/v1/me/working-unit", { unit_id: me.body.home_unit.id });
    expect(chosen.status).toBe(200);
    const stepUp = await call<{ error_code: string }>(page, "POST", "/api/v1/me/reauthentication");
    expect([stepUp.status, stepUp.body.error_code]).toEqual([403, "auth.impersonation_forbidden"]);
    const password = await call<{ error_code: string }>(page, "POST", "/api/v1/me/password", {
      current_password: person.password,
      new_password: "an impersonator's choice of password",
      end_other_sessions: true,
    });
    expect([password.status, password.body.error_code]).toEqual([403, "auth.impersonation_forbidden"]);

    // End: the administrator is themselves again, and the audit trail names both on the person's action.
    await band.getByRole("button", { name: "End" }).click();
    await expect(page.locator("[data-impersonation-ended]")).toBeVisible();
    const back = await call<{ id: string }>(page, "GET", "/api/v1/me");
    expect(back.body.id).toBe(administrator.body.id);
    const audit = await call<{
      data: { event_type: string; actor_id: string; impersonator_id: string; impersonator_name: string }[];
    }>(
      page,
      "GET",
      `/api/v1/audit-events?filter=impersonator_id:eq:${administrator.body.id}&sort=-created_at&page_size=20`,
    );
    const action = audit.body.data.find((event) => event.event_type === "identity.working_unit_chosen");
    expect(action?.actor_id).toBe(person.id);
    expect(action?.impersonator_name).toBe(administrator.body.display_name);

    // The person, in a browser of their own, finds who signed in as them, when and why.
    const context = await browser.newContext({ baseURL: testInfo.project.use.baseURL, ignoreHTTPSErrors: true });
    const theirs = await context.newPage();
    try {
      await signIn(theirs, { loginId: person.loginId, password: person.password });
      await open(theirs, "/me?tab=security");
      const activity = theirs.getByRole("table", { name: "Your security activity" });
      await expect(activity).toContainText("Someone signed in as you");
      await expect(activity).toContainText(reason);
      await expect(activity).toContainText("Signing in as you ended");
      await theirs.screenshot({ path: "test-results/e2e/impersonation-security-activity.png", fullPage: true });
    } finally {
      await context.close();
    }
  });
});
