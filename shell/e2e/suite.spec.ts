import { expect, test, type Page } from "@playwright/test";
import { signIn, suitesOfTenant } from "./helpers";

/**
 * TF-244 on a real unit (ADR-0059): the tenant's suites as the control plane records them, and what the shell shows for
 * them. Where the tenant has the eProcurement Suite, the home page shows its group and the About page names the release,
 * the installation and the plan; where it does not, there is no group and the suite's endpoint answers not found. The
 * control plane is reached as `make e2e` passes it (E2E_CONTROL_URL, E2E_OPERATOR_SECRET); the tenant is the one under test.
 */
const tenant =
  process.env.E2E_TENANT ?? new URL(process.env.E2E_BASE_URL ?? "https://smoke.localhost").hostname.split(".")[0];

/** What the unit's API answers for the suite's About endpoint, asked from the page with the browser's session. */
async function aboutStatus(page: Page): Promise<number> {
  return page.evaluate(async () => {
    const response = await fetch("/api/v1/suites/procurement/about", {
      credentials: "same-origin",
      headers: { Accept: "application/json" },
    });
    return response.status;
  });
}

test("the eProcurement Suite's group and About page are there exactly where the tenant has the suite [TF-244 AC1, TF-244 AC2, ADR-0059]", async ({
  page,
}) => {
  const suites = await suitesOfTenant(tenant);
  await signIn(page);
  await page.goto("/");
  await expect(page.getByRole("region", { name: "IronSaaS Foundation" })).toBeVisible();

  if (!suites.includes("procurement")) {
    await expect(page.getByRole("region", { name: "eProcurement Suite" })).toHaveCount(0);
    expect(await aboutStatus(page)).toBe(404);
    return;
  }

  // From the home page, as a person finds it: the suite's group, its module, the About page.
  const suite = page.getByRole("region", { name: "eProcurement Suite" });
  await suite.getByRole("link", { name: "About this suite" }).click();
  await expect(page).toHaveURL(/\/procurement\/about$/);
  await expect(page.getByRole("heading", { level: 2, name: "About the eProcurement Suite" })).toBeVisible();
  const installed = page.getByRole("table", { name: "The suite" }).getByRole("row", { name: /Installed/ });
  await expect(installed).toContainText(/release/);
  await expect(installed).toContainText(/By the platform's job [0-9a-f-]{36}/);
  await expect(page.getByText("eProcurement Essentials", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("table", { name: "The suite's modules" }).getByRole("row", { name: /Purchasing/ }),
  ).toContainText("In your plan");
  await page.screenshot({ path: "test-results/e2e/suite-about.png", fullPage: true });
});
