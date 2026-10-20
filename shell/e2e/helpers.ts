import { expect, type Page } from "@playwright/test";

export const loginId = process.env.E2E_LOGIN_ID ?? "admin@smoke.localhost";
export const password = process.env.E2E_PASSWORD ?? "";

/** Signs in through the real login page and waits for the shell: the administrator, unless another account is given. */
export async function signIn(
  page: Page,
  account: { loginId: string; password: string } = { loginId, password },
): Promise<void> {
  if (!account.password) {
    throw new Error("E2E_PASSWORD is required: the administrator's password, as the smoke tool kept it in the store.");
  }
  await page.goto("/login");
  await page.getByLabel(/login identifier/i).fill(account.loginId);
  await page.getByLabel(/^password/i).fill(account.password);
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

export interface Answer<T> {
  status: number;
  body: T;
}

/** A call from the page, with its cookie: Node's resolver behind page.request does not know *.localhost, Chromium does. */
export async function call<T>(page: Page, method: string, path: string, body?: unknown): Promise<Answer<T>> {
  return page.evaluate(
    async ({ method, path, body }) => {
      const headers: Record<string, string> = { Accept: "application/json" };
      const init: RequestInit = { method, headers };
      if (body !== undefined) {
        headers["Content-Type"] = "application/json";
        init.body = JSON.stringify(body);
      }
      const response = await fetch(path, init);
      const text = await response.text();
      return { status: response.status, body: (text ? JSON.parse(text) : null) as never };
    },
    { method, path, body },
  );
}
