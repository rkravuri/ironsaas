import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { stubSignedIn } from "./stubs";

const schemes = ["light", "dark"] as const;
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

for (const scheme of schemes) {
  test(`the Users page in the ${scheme} scheme: the grid by keyboard, the density switch, and no WCAG 2.1 AA violation`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubSignedIn(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/users");

    const grid = page.getByRole("region", { name: "Users" });
    // A row may render in more than one container (pinned columns); the first is the one that holds the cells.
    const row = (index: number) => grid.locator(`.ag-row[row-index="${index}"]`).first();
    await expect(row(0)).toBeVisible();
    await page.screenshot({ path: `test-results/users-${scheme}.png` });

    // Tab into the grid: the header first, then the first cell of the first row.
    await page.getByRole("radiogroup", { name: "Density" }).getByRole("radio", { name: "Compact" }).focus();
    for (let presses = 0; presses < 8 && (await grid.locator(".ag-cell-focus").count()) === 0; presses += 1) {
      await page.keyboard.press("Tab");
    }
    await expect(grid.locator(".ag-cell-focus")).toHaveCount(1);

    // Arrow to the second row, select it with Space, then with Enter; open its action with Ctrl+Enter.
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Space");
    await expect(row(1)).toHaveAttribute("aria-selected", "true");
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");
    await expect(row(2)).toHaveAttribute("aria-selected", "true");
    await expect(row(1)).not.toHaveAttribute("aria-selected", "true");
    // Ctrl+Enter opens the row's record page (A2); back is the list again.
    await page.keyboard.press("Control+Enter");
    await expect(page).toHaveURL(/\/users\/01a14e12-0000-7000-8000-000000000003$/);
    await expect(page.getByText("user03@acme.localhost")).toBeVisible();
    await page.goBack();
    await expect(row(0)).toBeVisible();

    // The density switch changes the row height. Back from the record, the grid renders its rows again, so the row's
    // height is read once it has a box, not at the first glance.
    let comfortable = 0;
    await expect.poll(async () => (comfortable = (await row(0).boundingBox())?.height ?? 0)).toBeGreaterThan(32);
    // The segmented control hides its radios; the visible label takes the click.
    await page.getByRole("radiogroup", { name: "Density" }).getByText("Compact").click();
    await expect.poll(async () => (await row(0).boundingBox())?.height).toBeLessThan(comfortable);
    await expect.poll(async () => (await row(0).boundingBox())?.height).toBe(32);

    const results = await new AxeBuilder({ page }).withTags(wcag21aa).analyze();
    expect(results.violations.map((violation) => `${violation.id}: ${violation.help} at ${violation.nodes.map((node) => node.target.map(String).join(" ")).join("; ")}`)).toEqual([]);
  });
}
