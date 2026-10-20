import { expect, test } from "@playwright/test";
import { loginId, open, signIn } from "./helpers";

/** The unit `make e2e` makes for the child administrator (infra/compose/scripts/e2e-data.py). */
const childUnit = process.env.E2E_CHILD_UNIT ?? "E2E branch";
const domain = loginId.split("@")[1] ?? "smoke.localhost";

test.describe("home and the header (D4)", () => {
  test("the administrator signs in and lands on the home page", async ({ page }) => {
    await signIn(page);

    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole("heading", { level: 1 })).toHaveText(/^Good (morning|afternoon|evening), /);
    await expect(page.getByRole("article", { name: "Identity" })).toBeVisible();
    await expect(page.getByRole("article", { name: "Console" })).toBeVisible();
    await expect(page.getByRole("region", { name: "Quick actions" }).getByRole("link", { name: "Invite a person" })).toBeVisible();
    await expect(page.getByRole("navigation", { name: "Main navigation" }).getByRole("link").first()).toHaveAccessibleName("Home");
    await page.screenshot({ path: "test-results/e2e/home-administrator.png", fullPage: true });
  });

  test("Ctrl+K, units, Enter opens the units page", async ({ page }) => {
    await signIn(page);

    await page.keyboard.press("Control+K");
    const search = page.getByRole("combobox", { name: "Search pages and actions" });
    await expect(search).toBeFocused();
    await search.fill("units");
    await page.keyboard.press("Enter");

    await expect(page).toHaveURL(/\/console\/units$/);
    await expect(page.getByRole("tree", { name: "Organisation units" })).toBeVisible();
  });

  test("invites a person into the child unit, shows the link once and opens the new record", async ({ page }) => {
    await signIn(page);
    await open(page, "/users");
    const name = `Invited ${Date.now().toString(36)}`;
    const login = `invited-${Date.now().toString(36)}@${domain}`;

    await page.getByRole("button", { name: "Invite a person" }).click();
    const dialog = page.getByRole("dialog", { name: "Invite a person" });
    await dialog.getByRole("textbox", { name: "Display name" }).fill(name);
    await dialog.getByRole("textbox", { name: /^Login identifier/ }).fill(login);
    await dialog.getByRole("tree", { name: "Home unit" }).getByText(childUnit, { exact: true }).click();
    await expect(dialog.getByText(new RegExp(`/ ${childUnit}$`))).toBeVisible();
    await dialog.getByRole("button", { name: "Invite" }).click();

    const done = page.getByRole("dialog", { name: `${name} is invited` });
    await expect(done.getByRole("textbox", { name: "Invitation link" })).toHaveValue(/\/invitations\/[^/]+$/);
    await expect(done).toContainText("Email delivery comes later");
    await page.screenshot({ path: "test-results/e2e/invite-link.png" });

    await done.getByRole("button", { name: "Open their record" }).click();
    await expect(page.getByRole("heading", { level: 2, name })).toBeVisible();
    await expect(page.locator("[data-panel=record-header]")).toContainText("Invited");
    await expect(page.locator("[data-panel=record-header]")).toContainText(childUnit);
    // Shown once: the record page does not carry the link.
    await expect(page.getByText(/\/invitations\//)).toHaveCount(0);
  });
});
