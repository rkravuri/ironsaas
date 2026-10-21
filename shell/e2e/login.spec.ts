import { expect, test } from "@playwright/test";
import { loginId, password, signIn } from "./helpers";

test.describe("login", () => {
  test("a wrong password shows the fixed message and a correlation id [TF-130 AC2, ADR-0014]", async ({ page }) => {
    await page.goto("/login");
    await page.getByLabel(/login identifier/i).fill(loginId);
    await page.getByLabel(/^password/i).fill("definitely-not-" + password);
    await page.getByRole("button", { name: /sign in/i }).click();

    const alert = page.getByRole("alert");
    await expect(alert).toContainText("The login details are not correct.");
    await expect(alert).toContainText(/correlation id/i);
  });

  test("the administrator signs in and sees the navigation from the manifests [ADR-0020]", async ({ page }) => {
    await signIn(page);

    const navigation = page.getByRole("navigation", { name: "Main navigation" });
    await expect(navigation.getByRole("link", { name: "Users" })).toBeVisible();
    await expect(navigation.getByRole("link", { name: "My profile" })).toBeVisible();
  });

  test("signing out ends the session [ADR-0033]", async ({ page }) => {
    await signIn(page);
    await page.getByRole("button", { name: /signed in as/i }).click();
    await page.getByRole("menuitem", { name: /sign out/i }).click();
    await expect(page.getByRole("status")).toHaveText("You have signed out.");

    await expect(page).toHaveURL(/\/login/);
  });
});
