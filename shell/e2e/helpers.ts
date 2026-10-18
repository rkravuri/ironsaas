import { expect, type Page } from "@playwright/test";

export const loginId = process.env.E2E_LOGIN_ID ?? "admin@smoke.localhost";
export const password = process.env.E2E_PASSWORD ?? "";

/** Signs in through the real login page and waits for the shell. */
export async function signIn(page: Page): Promise<void> {
  if (!password) {
    throw new Error("E2E_PASSWORD is required: the administrator's password, as the smoke tool kept it in the store.");
  }
  await page.goto("/login");
  await page.getByLabel(/login identifier/i).fill(loginId);
  await page.getByLabel(/^password/i).fill(password);
  await page.getByRole("button", { name: /sign in/i }).click();
  const shell = page.getByRole("heading", { name: "Foundation" });
  const crash = page.getByRole("heading", { name: /something went wrong/i });
  await expect(shell.or(crash)).toBeVisible();
  if (await crash.isVisible()) {
    throw new Error(`The shell crashed after login: ${await page.getByRole("alert").innerText()}`);
  }
}

/** Opens a page of the shell and fails with the error text if the page crashed instead of rendering. */
export async function open(page: Page, path: string): Promise<void> {
  await page.goto(path);
  const crash = page.getByRole("heading", { name: /something went wrong/i });
  await page.waitForLoadState("networkidle");
  if (await crash.isVisible()) {
    throw new Error(`${path} crashed: ${await page.getByRole("alert").innerText()}`);
  }
}
