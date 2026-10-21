import { expect, test } from "@playwright/test";
import { open, signIn } from "./helpers";

/**
 * Passwords and a person's life cycle on a real unit (S3 T2, ADR-0046). `make e2e` makes a person in the run's unit
 * (infra/compose/scripts/e2e-data.py); the administrator issues them a recovery link, they set a password with it in a
 * browser of their own and see it in their security activity, and the administrator deactivates them, which ends their
 * session. The teardown deactivates everyone else the run made.
 */
const person = { id: process.env.E2E_RECOVER_ID ?? "", loginId: process.env.E2E_RECOVER_LOGIN_ID ?? "" };

test.describe("passwords and a person's life cycle", () => {
  test.skip(person.id === "", "make e2e sets up the data first");

  test("an administrator's recovery link sets the person's password once, the person sees it in their security activity, and deactivation ends their session [TF-017 AC1, TF-017 AC2, TF-010 AC3, ADR-0046]", async ({
    page,
    browser,
  }) => {
    await signIn(page);
    await open(page, `/users/${person.id}`);
    const header = page.locator("[data-panel=record-header]");
    await header.getByRole("button", { name: "Issue a recovery link" }).click();
    await page
      .getByRole("dialog", { name: /^Issue a recovery link for/ })
      .getByRole("button", { name: "Issue the link" })
      .click();
    const shown = page.getByRole("dialog", { name: /^A recovery link for/ });
    const link = await shown.getByRole("textbox", { name: "Recovery link" }).inputValue();
    expect(link).toMatch(/\/recovery#[A-Za-z0-9_-]+$/);
    await page.screenshot({ path: "test-results/e2e/recovery-link-issued.png" });
    await shown.getByRole("button", { name: "Done" }).click();

    // The person, in a browser of their own: the link sets the password once.
    // A context of its own takes none of the project's options: the tenant's address comes from the link.
    const context = await browser.newContext({ ignoreHTTPSErrors: true, baseURL: new URL(link).origin });
    const theirs = await context.newPage();
    const chosen = `recovered on ${Date.now()} with a long phrase`;
    const setPassword = async (value: string) => {
      // Through a blank page: the same address again, fragment and all, would not load the page anew.
      await theirs.goto("about:blank");
      await theirs.goto(link);
      await theirs.getByLabel(/^New password/).fill(value);
      await theirs.getByLabel(/^Repeat the new password/).fill(value);
      await theirs.getByRole("button", { name: "Save the new password" }).click();
    };
    await setPassword(chosen);
    await expect(theirs.getByText(/every session of your account has ended/)).toBeVisible();
    await setPassword(`${chosen} again`);
    await expect(theirs.getByRole("alert")).toContainText("This recovery link does not work any more");

    // Signed in with it, the person finds both events in My profile, newest first.
    await signIn(theirs, { loginId: person.loginId, password: chosen });
    await open(theirs, "/me");
    await theirs.getByRole("tab", { name: "Security" }).click();
    const activity = theirs.getByRole("table", { name: "Your security activity" });
    await expect(activity.getByRole("row").nth(1)).toContainText("Password set with a recovery link");
    await expect(activity).toContainText("Recovery link issued");
    await theirs.screenshot({ path: "test-results/e2e/recovery-security-activity.png", fullPage: true });

    // Deactivated by the administrator: the person's next request finds the session ended.
    await header.getByRole("button", { name: "Deactivate" }).click();
    await page
      .getByRole("dialog", { name: /^Deactivate/ })
      .getByRole("button", { name: "Deactivate" })
      .click();
    await expect(header).toContainText("Deactivated");
    await expect(header.getByRole("button", { name: "Reactivate" })).toBeVisible();
    await theirs.goto("/me");
    await expect(theirs.getByRole("button", { name: /sign in/i })).toBeVisible();
    await context.close();
  });
});
