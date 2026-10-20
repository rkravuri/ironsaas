import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { stubConsole } from "./stubs";

const schemes = ["light", "dark"] as const;
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

for (const scheme of schemes) {
  test(`the organisation units page in the ${scheme} scheme: the tree by keyboard, the record follows, a dialog opens, and no WCAG 2.1 AA violation`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubConsole(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/units");

    const tree = page.getByRole("tree", { name: "Organisation units" });
    await expect(tree.getByRole("treeitem")).toHaveCount(9);
    await expect(page.getByRole("heading", { name: "Acme Corporation" })).toBeVisible();

    // Keyboard: into the tree, down to Operations; the record follows the focus.
    await tree.getByRole("treeitem").first().focus();
    for (let presses = 0; presses < 3; presses += 1) {
      await page.keyboard.press("ArrowDown");
    }
    await expect(page.getByRole("heading", { name: "Operations" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/units-${scheme}.png` });

    // On to the record's first action, which opens the form for a unit under Operations; Escape closes it.
    await page.keyboard.press("Tab");
    await expect(page.getByRole("button", { name: "Add a unit here" })).toBeFocused();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "A new unit under Operations" });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByLabel("Name")).toBeFocused();
    await page.screenshot({ path: `test-results/a11y/units-create-${scheme}.png` });
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();

    const results = await new AxeBuilder({ page }).withTags(wcag21aa).analyze();
    expect(results.violations.map((violation) => `${violation.id}: ${violation.help} at ${violation.nodes.map((node) => node.target.map(String).join(" ")).join("; ")}`)).toEqual([]);
  });
}
