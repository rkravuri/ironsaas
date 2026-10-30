import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { procurementAbout, stubProcurementSuite } from "./stubs";

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
  test(`the eProcurement Suite's About page in the ${scheme} scheme: the release, the installation, the plan and the module in words, its contents by keyboard, and no WCAG 2.1 AA violation [TF-244 AC1, TF-172 AC1, ADR-0024, ADR-0059]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubProcurementSuite(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/procurement/about");

    await expect(page.getByRole("heading", { level: 2, name: "About the eProcurement Suite" })).toBeVisible();
    const suite = page.getByRole("table", { name: "The suite" });
    await expect(suite.getByRole("row", { name: /Release/ })).toContainText("2026.10.30.7");
    await expect(suite.getByRole("row", { name: /Installed/ })).toContainText(
      `By the platform's job ${procurementAbout.installation.installed_by_job}`,
    );
    await expect(page.getByText("eProcurement Essentials", { exact: true })).toBeVisible();
    await expect(
      page.getByRole("table", { name: "The suite's modules" }).getByRole("row", { name: /Purchasing/ }),
    ).toContainText("In your plan");
    await expect(page.getByText("The suite has no features of its own yet")).toBeVisible();
    // Read only for the tenant: nothing on the page changes the suite or its plan.
    await expect(page.getByRole("main").getByRole("button")).toHaveCount(0);
    // The page is in the suite's navigation group.
    await expect(
      page.getByRole("navigation", { name: "Main navigation" }).getByRole("link", { name: "About this suite" }),
    ).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/suite-about-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    // Keyboard only: the contents list takes the person to a section.
    const contents = page.getByRole("navigation", { name: "On this page" });
    await contents.getByRole("link", { name: "The suite's features" }).focus();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/#suite-about-features$/);
  });

  test(`the home page of a tenant with the eProcurement Suite in the ${scheme} scheme shows the suite's group after the Foundation's, with no WCAG 2.1 AA violation [TF-244 AC1, TF-172 AC1, ADR-0033, ADR-0059]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubProcurementSuite(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/");

    const suite = page.getByRole("region", { name: "eProcurement Suite" });
    await expect(suite.getByRole("article", { name: "Purchasing" })).toBeVisible();
    await expect(suite.getByRole("link", { name: "About this suite" })).toHaveAttribute("href", "/procurement/about");
    await expect(page.getByRole("heading", { level: 3 })).toHaveText(["IronSaaS Foundation", "eProcurement Suite"]);
    await page.screenshot({ path: `test-results/a11y/home-suite-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });
}
