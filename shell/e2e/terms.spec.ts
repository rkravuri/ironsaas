import { expect, test, type Page } from "@playwright/test";
import { call, open, signIn, submitSignIn } from "./helpers";

/**
 * Terms acceptance on a real unit (S3 T5, TF-028, ADR-0049). The administrator publishes a document of the run in the
 * console, and accepts it, since they are held by it too; the run's person signs in and lands on it before any page,
 * declines and is signed out, signs in again and accepts, and reaches the shell; the administrator sees the acceptance
 * in the console. The document is retired at the end, whatever the result, so nobody else is asked to accept it.
 */
const person = { loginId: process.env.E2E_TERMS_LOGIN_ID ?? "", password: process.env.E2E_TERMS_PASSWORD ?? "" };
const run = process.env.E2E_RUN ?? Date.now().toString(36);

/** Signs in with a password alone, without expecting the shell: the terms come first. */
async function signInToTerms(page: Page): Promise<void> {
  await page.goto("/login");
  await page.getByLabel(/login identifier/i).fill(person.loginId);
  await page.getByLabel(/^password/i).fill(person.password);
  await submitSignIn(page, page.getByRole("heading", { name: "Read and accept" }));
}

test.describe("terms acceptance", () => {
  test.skip(person.loginId === "", "make e2e sets up the data first");

  test("a new version holds every page until it is accepted, declining signs out, and the administrator sees who accepted which version [TF-028 AC1, TF-028 AC2, TF-028 AC4, ADR-0049]", async ({
    page,
    browser,
  }, testInfo) => {
    test.setTimeout(120_000);
    const key = `e2e-terms-${run}`;
    const title = `Browser test terms ${run}`;
    await signIn(page);
    try {
      await open(page, "/console/documents");
      await page.getByRole("button", { name: "Publish a new document" }).click();
      const dialog = page.getByRole("dialog", { name: "Publish a new document" });
      await dialog.getByLabel(/^Key/).fill(key);
      await dialog.getByLabel(/^Title/).fill(title);
      await dialog.getByLabel(/^Text/).fill("The browser test asks you to accept this, once.");
      await dialog.getByRole("button", { name: "Publish" }).click();

      // The administrator is a person of the tenant too: the page's next call is refused, and the terms come first.
      await expect(page.getByRole("heading", { name: "Read and accept" })).toBeVisible();
      await page.getByRole("button", { name: "Accept" }).click();
      await expect(page.getByRole("heading", { name: "Documents" })).toBeVisible();

      // The person, in a browser of their own: the terms before any page; declining signs them out.
      const context = await browser.newContext({ baseURL: testInfo.project.use.baseURL, ignoreHTTPSErrors: true });
      const theirs = await context.newPage();
      try {
        await signInToTerms(theirs);
        await expect(theirs.getByRole("article", { name: title })).toContainText("Your organisation's");
        await theirs.screenshot({ path: "test-results/e2e/terms-held.png", fullPage: true });
        const held = await call<{ error_code: string }>(theirs, "GET", "/api/v1/directory?search=ad");
        expect([held.status, held.body.error_code]).toEqual([403, "identity.terms.acceptance_required"]);
        await theirs.getByRole("button", { name: "Decline and sign out" }).click();
        await theirs
          .getByRole("dialog", { name: "Decline and sign out?" })
          .getByRole("button", { name: "Decline and sign out" })
          .click();
        await expect(theirs.getByText("You declined a document you must accept")).toBeVisible();

        // Again: still held until they accept, then the shell.
        await signInToTerms(theirs);
        await theirs.getByRole("button", { name: "Accept" }).click();
        await expect(theirs.getByRole("heading", { name: "IronSaaS", exact: true })).toBeVisible();
      } finally {
        await context.close();
      }

      // The administrator sees the person's acceptance, version 1, in the console.
      await open(page, "/console/documents");
      await page.getByRole("combobox", { name: "Document" }).click();
      await page.getByRole("option", { name: title }).click();
      const grid = page.getByRole("region", { name: "Acceptances" });
      await expect(grid).toContainText(person.loginId);
      await page.screenshot({ path: "test-results/e2e/terms-acceptances.png", fullPage: true });
    } finally {
      const retired = await call(page, "POST", `/api/v1/documents/${key}/retire`);
      expect([204, 404]).toContain(retired.status);
    }
  });
});
