import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { record, stubAccess } from "./stubs";

const schemes = ["light", "dark"] as const;
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

async function expectNoViolation(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(wcag21aa).analyze();
  expect(results.violations.map((violation) => `${violation.id}: ${violation.help} at ${violation.nodes.map((node) => node.target.map(String).join(" ")).join("; ")}`)).toEqual([]);
}

for (const scheme of schemes) {
  test(`the user record page in the ${scheme} scheme: the Access tab and the grant dialog by keyboard, and no WCAG 2.1 AA violation`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubAccess(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto(`/users/${record.id}`);

    await expect(page.getByRole("heading", { name: record.display_name })).toBeVisible();
    await expect(page.getByText("Operations").first()).toBeVisible();

    // The tabs by keyboard: from Details, the right arrow opens Access.
    await page.getByRole("tab", { name: "Details" }).focus();
    await page.keyboard.press("ArrowRight");
    await expect(page.getByRole("tab", { name: "Access" })).toHaveAttribute("aria-selected", "true");
    const table = page.getByRole("table", { name: `Roles of ${record.display_name}` });
    await expect(table.getByRole("row")).toHaveCount(4);
    await expect(table).toContainText("This unit and the units below it");
    await expect(table).toContainText("Acme Corporation / Operations / North");
    await page.screenshot({ path: `test-results/user-record-${scheme}.png` });
    await expectNoViolation(page);

    // Grant a role: the dialog opens with the role field focused, and the unit picker is a tree.
    await page.getByRole("button", { name: "Grant a role" }).focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: `Grant a role to ${record.display_name}` });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("combobox", { name: "Role" })).toBeFocused();
    await expect(dialog.getByRole("tree", { name: "Unit" })).toBeVisible();
    await page.screenshot({ path: `test-results/user-grant-${scheme}.png` });
    await expectNoViolation(page);
    // The searchable role field opens its list on focus: the first Escape closes the list, the second the dialog.
    await page.keyboard.press("Escape");
    await expect(dialog.getByRole("combobox", { name: "Role" })).toHaveAttribute("aria-expanded", "false");
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();

    // Remove sits behind a confirmation.
    await page.getByRole("button", { name: "Remove User reader" }).click();
    const confirm = page.getByRole("dialog", { name: "Remove User reader?" });
    await expect(confirm).toContainText("loses User reader");
    await page.keyboard.press("Escape");
    await expect(confirm).toBeHidden();
  });
}
