import { randomBytes } from "node:crypto";
import { expect, test } from "@playwright/test";
import { call, signIn } from "./helpers";

const run = process.env.E2E_RUN ?? Date.now().toString(36);
const runUnit = process.env.E2E_RUN_UNIT ?? "";

/**
 * Single sign-on through the test provider (navikt's mock-oauth2-server, profile sso, ADR-0048), which make e2e starts:
 * the administrator connects it in the console and tests it; a new employee of its domain signs in through it with the
 * authorization code flow and PKCE, in a browser of their own, and arrives signed in, created in the run's unit; the
 * administrator removes the provider. The run's teardown deactivates the person, as everyone homed in the run's unit.
 */
test("an administrator connects the test provider and tests it, and a new employee signs in through it and is created at the first sign-in [TF-014 AC1, TF-014 AC6, ADR-0048]", async ({
  page,
  browser,
}, testInfo) => {
  test.setTimeout(120_000);
  const name = `E2E provider ${run}`;
  const domain = `e2e-${run}.sso.test`;
  const email = `new-employee-${run}@${domain}`;
  await signIn(page);

  await page.goto("/console/sso/new");
  await page.getByLabel("Name", { exact: true }).fill(name);
  await page.getByLabel("Discovery address").fill(`http://idp.localhost/e2e-${run}/.well-known/openid-configuration`);
  await page.getByRole("button", { name: "Test connection" }).click();
  await expect(page.getByText(/^The connection works\./)).toBeVisible();
  await expect(page.getByText(/^Passed: Signing keys\./)).toBeVisible();
  await page.getByLabel("Client ID").fill("iron-e2e");
  await page.getByLabel("Client secret").fill(randomBytes(18).toString("base64url"));
  const domains = page.getByRole("combobox", { name: "Allowed email domains" });
  await domains.fill(domain);
  await domains.press("Enter");
  const create = page.getByRole("switch", { name: "Create an account at the first sign-in" });
  await create.focus();
  await page.keyboard.press("Space");
  await expect(create).toBeChecked();
  if (runUnit) {
    await page.getByRole("combobox", { name: "Home unit of new people" }).fill(runUnit);
    await page.getByRole("option", { name: new RegExp(`${runUnit}$`) }).click();
  }
  await page.getByRole("button", { name: "Connect the provider" }).click();
  await expect(page.getByRole("heading", { name })).toBeVisible();

  // A new employee, in a browser of their own: the company login's button, the provider's page, and back signed in.
  const context = await browser.newContext({ baseURL: testInfo.project.use.baseURL, ignoreHTTPSErrors: true });
  const theirs = await context.newPage();
  try {
    await theirs.goto("/login");
    await theirs.getByRole("button", { name: `Continue with ${name}` }).click();
    await theirs.locator('input[name="username"]').fill(`new-employee-${run}`);
    await theirs
      .locator('textarea[name="claims"]')
      .fill(JSON.stringify({ email, email_verified: true, name: `New employee ${run}` }));
    await theirs.getByRole("button", { name: "Sign-in" }).click();
    await expect(theirs.getByRole("heading", { name: "IronSaaS", exact: true })).toBeVisible();
    const me = await call<{ login_id: string; display_name: string; home_unit: { name: string } | null }>(
      theirs,
      "GET",
      "/api/v1/me",
    );
    expect(me.status).toBe(200);
    expect(me.body.login_id).toBe(email);
    expect(me.body.display_name).toBe(`New employee ${run}`);
    if (runUnit) {
      expect(me.body.home_unit?.name).toBe(runUnit);
    }
  } finally {
    await context.close();
  }

  // The administrator removes the provider: its people keep their accounts, and stop signing in through it.
  await page.getByRole("button", { name: "Remove the provider" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Remove" }).click();
  await expect(page).toHaveURL(/\/console\/sso$/);
});
