import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { stubKeys } from "./stubs";

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
  test(`the keys page in the ${scheme} scheme: each key's version, rotations and re-encryption in words, its contents by keyboard, and no WCAG 2.1 AA violation [ADR-0054, ADR-0024]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubKeys(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/keys");

    await expect(page.getByRole("heading", { level: 2, name: "Keys" })).toBeVisible();
    const current = page.getByRole("table", { name: "The keys" });
    const data = current.getByRole("row", { name: /Data key/ });
    await expect(data).toContainText("Version 2");
    await expect(data.getByRole("progressbar", { name: "1,200 of 3,000 values moved to version 2" })).toBeVisible();
    await expect(current.getByRole("row", { name: /Index key/ })).toContainText("All moved to version 3");
    await expect(current.getByRole("row", { name: /Database password/ })).toContainText("Every 90 days");
    const rotations = page.getByRole("table", { name: "Latest rotations" });
    await expect(rotations.getByRole("row", { name: /Data key/ })).toContainText("Olga Operator");
    await expect(rotations.getByRole("row", { name: /Index key/ })).toContainText("Old version retired");
    // Read only: rotation is the operator's.
    await expect(page.getByRole("main").getByRole("button")).toHaveCount(0);
    await expect(
      page.getByRole("navigation", { name: "Main navigation" }).getByRole("link", { name: "Keys" }),
    ).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/keys-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    // Keyboard only: the contents list takes the person to the rotations.
    const contents = page.getByRole("navigation", { name: "On this page" });
    await contents.getByRole("link", { name: "Latest rotations" }).focus();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/#keys-rotations$/);
  });

  test(`the keys page in the ${scheme} scheme says in words when the keys' state has not arrived, with no WCAG 2.1 AA violation [ADR-0054, ADR-0024]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubKeys(page, { keys: [], rotations: [], asked_at: null });
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/keys");

    await expect(page.getByText("No keys to show yet")).toBeVisible();
    await expect(page.getByText("No rotation yet")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/keys-empty-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });
}
