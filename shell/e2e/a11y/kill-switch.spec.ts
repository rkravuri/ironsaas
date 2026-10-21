import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { contextImportsKilled, stubAccess } from "./stubs";

const schemes = ["light", "dark"] as const;
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

async function expectNoViolation(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(wcag21aa).analyze();
  expect(
    results.violations.map(
      (violation) =>
        `${violation.id}: ${violation.help} at ${violation.nodes.map((node) => node.target.map(String).join(" ")).join("; ")}`,
    ),
  ).toEqual([]);
}

for (const scheme of schemes) {
  test(`the kill-switch banner in the ${scheme} scheme: the path, since when and the reason in words above the page, Import hidden, and no WCAG 2.1 AA violation [TF-172 AC1, ADR-0035]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubAccess(page);
    // Routed after the console's stubs, so this answer wins (Playwright tries the newest route first).
    await page.route("**/api/v1/me/context", (route) => route.fulfill({ json: contextImportsKilled }));
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/console/roles");
    // The work under the banner, loaded, so the screenshot shows the banner above a real page.
    await expect(page.getByRole("region", { name: "Roles" }).locator('.ag-row[row-index="0"]').first()).toContainText(
      "Operations lead",
    );
    const banner = page.getByRole("status").filter({ hasText: "has stopped imports" });
    await expect(banner).toBeVisible();
    await expect(banner).toContainText("The platform's operator has stopped imports for now");
    await expect(banner).toContainText(/Since .+2026/);
    await expect(banner).toContainText("The operator's reason: A fault in role import is being fixed");
    // The banner is in the page's flow: the page's heading starts below it, nothing is covered.
    const heading = page.getByRole("heading", { level: 2, name: "Roles" });
    const [bannerBox, headingBox] = [await banner.boundingBox(), await heading.boundingBox()];
    expect(bannerBox!.y + bannerBox!.height).toBeLessThanOrEqual(headingBox!.y);
    // The rest of the page works: Export and New role stay, Import is hidden while imports are stopped.
    await expect(page.getByRole("button", { name: "Export roles" })).toBeVisible();
    await expect(page.getByRole("link", { name: /New role/ })).toBeVisible();
    await expect(page.getByRole("button", { name: "Import roles" })).toHaveCount(0);
    await page.screenshot({ path: `test-results/a11y/kill-switch-banner-${scheme}.png` });
    await expectNoViolation(page);
  });
}
