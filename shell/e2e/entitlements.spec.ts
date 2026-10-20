import { expect, request as playwrightRequest, test, type APIRequestContext, type Page } from "@playwright/test";
import { signIn } from "./helpers";

/**
 * ADR-0034 on a real unit: the operator moves the tenant from Enterprise to Standard in the control plane, and within a
 * minute role export and import are gone from the shell and refused by the API; moved back, they return. The control
 * plane is reached as `make e2e` passes it (E2E_CONTROL_URL, E2E_OPERATOR_SECRET); the tenant is the one under test.
 */
const controlUrl = process.env.E2E_CONTROL_URL ?? "http://localhost:5200";
const operatorSecret = process.env.E2E_OPERATOR_SECRET ?? "";
const tenant =
  process.env.E2E_TENANT ?? new URL(process.env.E2E_BASE_URL ?? "https://smoke.localhost").hostname.split(".")[0];
const minute = 60_000;

async function applyPlan(plan: "standard" | "enterprise"): Promise<number> {
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
    const applied = await control.post(`/api/v1/tenants/${tenant}/plan`, {
      data: { plan, dry_run: false },
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    expect(applied.ok(), await applied.text()).toBe(true);
    return Date.now();
  } finally {
    await control.dispose();
  }
}

/**
 * What the unit's API answers for role export, asked from the page: the browser's session cookie and its name
 * resolution (a .localhost name is the laptop to the browser, not to Node).
 */
async function exportAnswer(page: Page): Promise<string> {
  return page.evaluate(async () => {
    const response = await fetch("/api/v1/roles/export", {
      credentials: "same-origin",
      headers: { Accept: "application/json" },
    });
    if (response.ok) {
      return "ok";
    }
    const problem = (await response.json()) as { error_code?: string };
    return `${response.status} ${problem.error_code ?? ""}`;
  });
}

test.describe("a plan changed in the control plane reaches the shell and the API within a minute", () => {
  // The worst case is two reads of thirty seconds in a row, twice, plus the sign-in.
  test.setTimeout(5 * minute);

  test.afterAll(async () => {
    // Whatever happened above, the tenant leaves this test on the plan it came with.
    await applyPlan("enterprise");
  });

  test("Enterprise to Standard removes role export and import, and back to Enterprise restores them", async ({
    page,
  }) => {
    await applyPlan("enterprise");
    await signIn(page);
    await page.goto("/console/roles");
    const exportButton = page.getByRole("button", { name: "Export roles" });
    const importButton = page.getByRole("button", { name: "Import roles" });
    await expect(exportButton).toBeVisible({ timeout: minute });
    await expect(importButton).toBeVisible();

    const toStandard = await applyPlan("standard");
    await expect(exportButton).toBeHidden({ timeout: minute });
    await expect(importButton).toBeHidden();
    const hiddenAfter = Date.now() - toStandard;
    await expect
      .poll(() => exportAnswer(page), { timeout: minute, intervals: [2_000] })
      .toBe("403 entitlements.feature_disabled");
    // The page stays, without the two actions: nothing else of the roles page needs the feature.
    await expect(page.getByRole("heading", { level: 2, name: "Roles" })).toBeVisible();

    const toEnterprise = await applyPlan("enterprise");
    await expect(exportButton).toBeVisible({ timeout: minute });
    await expect(importButton).toBeVisible();
    const shownAfter = Date.now() - toEnterprise;
    await expect.poll(() => exportAnswer(page), { timeout: minute, intervals: [2_000] }).toBe("ok");

    const latency = `hidden after ${Math.round(hiddenAfter / 1000)} s, shown again after ${Math.round(shownAfter / 1000)} s`;
    test.info().annotations.push({ type: "latency", description: latency });
    console.log(`Role transfer in the shell: ${latency}.`);
    expect(hiddenAfter).toBeLessThanOrEqual(minute);
    expect(shownAfter).toBeLessThanOrEqual(minute);
  });
});

test.describe("the console's Subscription page on a unit", () => {
  for (const scheme of ["light", "dark"] as const) {
    test(`shows the tenant's plan and role transfer included, in the ${scheme} scheme`, async ({ page }) => {
      await signIn(page);
      await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
      await page.goto("/console/subscription");

      await expect(page.getByRole("heading", { level: 2, name: "Subscription" })).toBeVisible();
      await expect(page.getByText("Enterprise", { exact: true })).toBeVisible();
      await expect(page.getByText("No licensed modules yet")).toBeVisible();
      const transfer = page
        .getByRole("table", { name: "Features" })
        .getByRole("row", { name: /Export the tenant's roles/ });
      await expect(transfer).toContainText("Included");
      await expect(transfer).toContainText("In your plan");
      // Beside the browser tests' own output, which each run empties: the page as the unit serves it.
      await page.screenshot({ path: `test-results/subscription-${tenant}-${scheme}.png`, fullPage: true });
    });
  }
});
