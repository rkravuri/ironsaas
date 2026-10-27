import { expect, request as playwrightRequest, test, type APIRequestContext, type Page } from "@playwright/test";
import { signIn } from "./helpers";

/**
 * ADR-0035 on a real unit: the platform operator turns the import kill switch on for the tenant under test in the
 * control plane, and within a minute the administrator sees the banner with the reason, Import is gone from the Roles
 * page, and the unit's API refuses an import with flags.killed; turned off, all three return. The control plane is
 * reached as `make e2e` passes it (E2E_CONTROL_URL, E2E_OPERATOR_SECRET).
 */
const controlUrl = process.env.E2E_CONTROL_URL ?? "http://localhost:5200";
const operatorSecret = process.env.E2E_OPERATOR_SECRET ?? "";
const tenant =
  process.env.E2E_TENANT ?? new URL(process.env.E2E_BASE_URL ?? "https://smoke.localhost").hostname.split(".")[0];
const minute = 60_000;
const reason = "The browser test checks the import kill switch; it is off again in a minute.";

async function killImports(on: boolean): Promise<number> {
  if (!operatorSecret) {
    throw new Error("E2E_OPERATOR_SECRET is required: the control plane's operator secret, which make e2e passes.");
  }
  const control: APIRequestContext = await playwrightRequest.newContext({ baseURL: controlUrl });
  try {
    const token = await control.post("/connect/token", {
      form: { grant_type: "client_credentials" },
      headers: { Authorization: `Basic ${Buffer.from(`operator:${operatorSecret}`).toString("base64")}` },
    });
    expect(token.ok(), await token.text()).toBe(true);
    const { access_token: accessToken } = (await token.json()) as { access_token: string };
    const flipped = await control.put("/api/v1/kill-switches/imports", {
      data: { on, reason: on ? reason : "The browser test is done.", tenant },
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    expect(flipped.ok(), await flipped.text()).toBe(true);
    return Date.now();
  } finally {
    await control.dispose();
  }
}

/**
 * The tenant's roles as their export gives them, asked once from the page with the browser's session cookie and its name
 * resolution: exports have a limit of their own (20 an hour per person, ADR-0053), which polling the export would spend.
 */
async function exportRoles(page: Page): Promise<unknown> {
  return page.evaluate(async () => {
    const exported = await fetch("/api/v1/roles/export", {
      credentials: "same-origin",
      headers: { Accept: "application/json" },
    });
    if (!exported.ok) {
      throw new Error(`The roles' export answered ${exported.status}.`);
    }
    return (await exported.json()) as unknown;
  });
}

/** What the unit's API answers for a role import of that export as a dry run, so nothing changes when it is allowed. */
async function importAnswer(page: Page, document: unknown): Promise<string> {
  return page.evaluate(async (document) => {
    const response = await fetch("/api/v1/roles/import", {
      method: "POST",
      credentials: "same-origin",
      headers: { Accept: "application/json", "Content-Type": "application/json" },
      body: JSON.stringify({ dry_run: true, document }),
    });
    if (response.ok) {
      return "ok";
    }
    const problem = (await response.json()) as { error_code?: string };
    return `${response.status} ${problem.error_code ?? ""}`;
  }, document);
}

test.describe("the import kill switch reaches the shell and the API within a minute", () => {
  // The worst case is a store read and a context read of thirty seconds each, twice, plus the sign-in.
  test.setTimeout(5 * minute);

  test.afterAll(async () => {
    // Whatever happened above, the tenant's imports leave this test running.
    await killImports(false);
  });

  test("on: the banner says why, Import is hidden and the API refuses it; off: all three return [TF-054 AC1, TF-054 AC2, ADR-0035]", async ({
    page,
  }) => {
    await killImports(false);
    await signIn(page);
    await page.goto("/console/roles");
    const importButton = page.getByRole("button", { name: "Import roles" });
    const banner = page.getByRole("status").filter({ hasText: "has stopped imports" });
    await expect(importButton).toBeVisible({ timeout: minute });
    await expect(banner).toHaveCount(0);
    const roles = await exportRoles(page);

    const on = await killImports(true);
    await expect(banner).toBeVisible({ timeout: minute });
    await expect(banner).toContainText(`The operator's reason: ${reason}`);
    await expect(importButton).toBeHidden();
    const shownAfter = Date.now() - on;
    await expect
      .poll(() => importAnswer(page, roles), { timeout: minute, intervals: [2_000] })
      .toBe("503 flags.killed");
    const refusedAfter = Date.now() - on;
    // Everything else of the page stays: export and the list.
    await expect(page.getByRole("button", { name: "Export roles" })).toBeVisible();
    await expect(page.getByRole("heading", { level: 2, name: "Roles" })).toBeVisible();
    await page.screenshot({ path: `test-results/kill-switch-${tenant}.png` });

    const off = await killImports(false);
    await expect(banner).toBeHidden({ timeout: minute });
    await expect(importButton).toBeVisible();
    const goneAfter = Date.now() - off;
    await expect.poll(() => importAnswer(page, roles), { timeout: minute, intervals: [2_000] }).toBe("ok");
    const allowedAfter = Date.now() - off;

    const latency = `banner after ${Math.round(shownAfter / 1000)} s, refused after ${Math.round(refusedAfter / 1000)} s; off: banner gone after ${Math.round(goneAfter / 1000)} s, allowed after ${Math.round(allowedAfter / 1000)} s`;
    test.info().annotations.push({ type: "latency", description: latency });
    console.log(`The import kill switch: ${latency}.`);
    for (const after of [shownAfter, refusedAfter, goneAfter, allowedAfter]) {
      expect(after).toBeLessThanOrEqual(minute);
    }
  });
});
