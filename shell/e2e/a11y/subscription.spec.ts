import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { stubSubscription, subscription } from "./stubs";

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
  test(`the subscription page in the ${scheme} scheme: the plan, modules and features in words, its contents by keyboard, and no WCAG 2.1 AA violation`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubSubscription(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/subscription");

    await expect(page.getByRole("heading", { level: 2, name: "Subscription" })).toBeVisible();
    await expect(page.getByText("Enterprise", { exact: true })).toBeVisible();
    const modules = page.getByRole("table", { name: "Licensed modules" });
    await expect(modules.getByRole("row", { name: /sourcing/ })).toContainText("48 of 50 used, 2 left");
    await expect(modules.getByRole("row", { name: /sourcing/ })).toContainText(/Ended on .+; keeps working until/);
    const features = page.getByRole("table", { name: "Features" });
    await expect(features.getByRole("row", { name: /Export the tenant's roles/ })).toContainText("Included");
    await expect(features.getByRole("row", { name: /Auctions/ })).toContainText("Not in your plan");
    // Read only for the tenant: nothing on the page changes the plan.
    await expect(page.getByRole("main").getByRole("button")).toHaveCount(0);
    // The page is in the console's navigation.
    await expect(
      page.getByRole("navigation", { name: "Main navigation" }).getByRole("link", { name: "Subscription" }),
    ).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/subscription-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    // Keyboard only: the contents list takes the person to a section.
    const contents = page.getByRole("navigation", { name: "On this page" });
    await contents.getByRole("link", { name: "Features" }).focus();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/#subscription-features$/);
  });

  test(`the subscription page in the ${scheme} scheme warns in words when it cannot be confirmed, with no WCAG 2.1 AA violation`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubSubscription(page, {
      ...subscription,
      modules: [],
      stale: true,
      features: subscription.features.map((feature) => ({ ...feature, status: "disabled", reason: "unknown" })),
    });
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/subscription");

    await expect(page.getByRole("alert")).toContainText("The subscription cannot be confirmed");
    await expect(page.getByText("No licensed modules yet")).toBeVisible();
    await expect(page.getByRole("table", { name: "Features" })).toContainText("Cannot be confirmed right now");
    await page.screenshot({ path: `test-results/a11y/subscription-stale-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });
}
