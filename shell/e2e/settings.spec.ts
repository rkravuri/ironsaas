import { expect, test, type Browser, type Page } from "@playwright/test";
import { loginId, open, signIn } from "./helpers";

/**
 * ADR-0036 on a real unit: the child unit's invitation validity set on the console's Settings page, and an invitation
 * into that unit expires when it says; the tenant's idle limit set to 30 minutes, and a new sign-in's /me answers it, so
 * the session warning follows; and a colour scheme chosen in one browser applies in another after sign-in. Every value
 * set here is reset at the end, whatever happened.
 */
const childUnit = process.env.E2E_CHILD_UNIT ?? "E2E branch";
const domain = loginId.split("@")[1] ?? "smoke.localhost";
const day = 24 * 60 * 60_000;

/** A call to the unit's API from the page, with the browser's session cookie and its name resolution. */
async function call(page: Page, method: string, path: string, body?: unknown): Promise<{ status: number; json: unknown; date: string | null }> {
  return page.evaluate(
    async ({ method, path, body }) => {
      const response = await fetch(path, {
        method,
        credentials: "same-origin",
        headers: { Accept: "application/json", ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      });
      const text = await response.text();
      return { status: response.status, json: text ? (JSON.parse(text) as unknown) : null, date: response.headers.get("Date") };
    },
    { method, path, body },
  );
}

async function childUnitId(page: Page): Promise<string> {
  const units = (await call(page, "GET", "/api/v1/organisation-units")).json as { data: { id: string; name: string }[] };
  const unit = units.data.find((candidate) => candidate.name === childUnit);
  expect(unit, `${childUnit} is the unit make e2e makes`).toBeDefined();
  return unit!.id;
}

async function signedInPage(browser: Browser, colorScheme: "light" | "dark" = "light"): Promise<Page> {
  // A context of its own is a second browser: it shares nothing with the test's page, the config's options included.
  const context = await browser.newContext({ ignoreHTTPSErrors: true, colorScheme, baseURL: process.env.E2E_BASE_URL ?? "https://smoke.localhost" });
  const page = await context.newPage();
  await signIn(page);
  return page;
}

test.describe("settings on a unit (ADR-0036)", () => {
  test.setTimeout(3 * 60_000);

  test.afterAll(async ({ browser }) => {
    // Whatever happened above, the tenant leaves this test with nothing set by it.
    const page = await signedInPage(browser);
    const unit = await childUnitId(page);
    await call(page, "DELETE", `/api/v1/settings/identity.invitation.valid_days?scope=unit&unit=${unit}`);
    await call(page, "DELETE", "/api/v1/settings/identity.session.idle_minutes?scope=tenant");
    await call(page, "DELETE", "/api/v1/settings/shell.colour_scheme?scope=user");
    await page.context().close();
  });

  test("a child unit's invitation validity, set on the Settings page, is what an invitation into it takes", async ({ page }) => {
    await signIn(page);
    await open(page, "/console/settings");
    await page.locator("label", { hasText: "One unit" }).click();
    await page.getByRole("tree").getByText(childUnit, { exact: true }).click();
    const row = page.getByRole("article", { name: "An invitation link stays valid for" });
    await row.getByRole("button", { name: /^Change/ }).click();
    const dialog = page.getByRole("dialog", { name: /^Change/ });
    await expect(dialog).toContainText(`/ ${childUnit}.`);
    await dialog.getByRole("textbox", { name: "Value" }).fill("5");
    await dialog.getByRole("button", { name: "Save" }).click();
    await expect(row).toContainText("5 days");
    await expect(row).toContainText(/Set here by .+ on /);

    // Invited into the unit: the link expires five days from now, as the unit says, not in the default three.
    await open(page, "/users");
    await page.getByRole("button", { name: "Invite a person" }).click();
    const invite = page.getByRole("dialog", { name: "Invite a person" });
    const name = `Validity ${Date.now().toString(36)}`;
    await invite.getByRole("textbox", { name: "Display name" }).fill(name);
    await invite.getByRole("textbox", { name: /^Login identifier/ }).fill(`validity-${Date.now().toString(36)}@${domain}`);
    await invite.getByRole("tree", { name: "Home unit" }).getByText(childUnit, { exact: true }).click();
    const created = page.waitForResponse((response) => response.request().method() === "POST" && response.url().endsWith("/api/v1/users"));
    await invite.getByRole("button", { name: "Invite" }).click();
    const answer = (await (await created).json()) as { invitation_expires_at: string };
    const expires = new Date(answer.invitation_expires_at).getTime();
    expect(Math.abs(expires - (Date.now() + 5 * day))).toBeLessThan(5 * 60_000);
    const done = page.getByRole("dialog", { name: `${name} is invited` });
    await expect(done).toContainText(`expires on ${await page.evaluate((at) => new Date(at).toLocaleString(), answer.invitation_expires_at)}`);
    await page.screenshot({ path: "test-results/settings-invitation-echo.png" });
  });

  test("the tenant's idle limit set to 30 minutes is what a new sign-in's session takes", async ({ page, browser }) => {
    await signIn(page);
    await open(page, "/console/settings");
    const row = page.getByRole("article", { name: "A session ends without activity after" });
    await row.getByRole("button", { name: /^Change/ }).click();
    const dialog = page.getByRole("dialog", { name: /^Change/ });
    await dialog.getByRole("textbox", { name: "Value" }).fill("30");
    await dialog.getByRole("button", { name: "Save" }).click();
    await expect(row).toContainText("30 minutes");
    await expect(dialog).toBeHidden();
    await page.screenshot({ path: "test-results/settings-echo.png" });

    const fresh = await signedInPage(browser);
    const me = await call(fresh, "GET", "/api/v1/me");
    const session = (me.json as { session: { idle_expires_at: string } }).session;
    const idle = new Date(session.idle_expires_at).getTime() - new Date(me.date ?? Date.now()).getTime();
    // The server's clock, by its Date header: within a minute either way of 30 minutes (its touch interval).
    expect(Math.abs(idle - 30 * 60_000)).toBeLessThan(90_000);
    await fresh.context().close();
  });

  test("a colour scheme chosen in one browser applies in another after sign-in", async ({ page, browser }) => {
    await signIn(page);
    await open(page, "/me");
    await page.getByRole("tab", { name: "Preferences" }).click();
    const saved = page.waitForResponse((response) => response.request().method() === "PUT" && response.url().includes("/api/v1/settings/shell.colour_scheme"));
    await page.getByRole("tabpanel", { name: "Preferences" }).locator("label", { hasText: "Dark" }).click();
    expect((await saved).status()).toBe(200);

    // Another browser on a light device: after sign-in the person's own choice applies, and stays for the next paint.
    const other = await signedInPage(browser, "light");
    await expect(other.locator("html")).toHaveAttribute("data-mantine-color-scheme", "dark");
    expect(await other.evaluate(() => window.localStorage.getItem("iron.scheme"))).toBe("dark");
    await other.reload();
    await expect(other.locator("html")).toHaveAttribute("data-mantine-color-scheme", "dark");
    await other.context().close();
  });
});
