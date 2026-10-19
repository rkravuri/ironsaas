import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { roles, stubAccess } from "./stubs";

const schemes = ["light", "dark"] as const;
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

async function expectNoViolation(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(wcag21aa).analyze();
  expect(results.violations.map((violation) => `${violation.id}: ${violation.help} at ${violation.nodes.map((node) => node.target.map(String).join(" ")).join("; ")}`)).toEqual([]);
}

const tenantRole = roles.find((role) => role.code === "operations-lead")!;
const systemRole = roles.find((role) => role.code === "tenant-administrator")!;

for (const scheme of schemes) {
  test(`the roles list and the role editor in the ${scheme} scheme: a permission by keyboard, a system role read only, and no WCAG 2.1 AA violation`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubAccess(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/console/roles");
    const grid = page.getByRole("region", { name: "Roles" });
    await expect(grid.locator('.ag-row[row-index="0"]').first()).toContainText("Operations lead");
    await expect(page.getByRole("button", { name: "Import roles" })).toBeVisible();
    await page.screenshot({ path: `test-results/roles-${scheme}.png` });
    await expectNoViolation(page);

    // A tenant role: the permissions by module, each risk in words; Space ticks a permission.
    await page.goto(`/console/roles/${tenantRole.id}`);
    await expect(page.getByRole("heading", { name: "Operations lead" })).toBeVisible();
    const authorization = page.getByRole("group", { name: "Authorization" });
    await expect(authorization).toContainText("High risk");
    const assign = page.getByRole("checkbox", { name: "Assign roles to principals and remove assignments" });
    await assign.focus();
    await page.keyboard.press("Space");
    await expect(assign).toBeChecked();
    await page.screenshot({ path: `test-results/role-editor-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    // A system role says why it cannot change, and offers no way to.
    await page.goto(`/console/roles/${systemRole.id}`);
    await expect(page.getByText(/System roles come with Foundation/)).toBeVisible();
    await expect(page.getByRole("button", { name: "Save changes" })).toHaveCount(0);
    await expect(page.getByRole("checkbox", { name: "Create, change and delete tenant roles" })).toBeDisabled();
    await page.screenshot({ path: `test-results/role-system-${scheme}.png` });
    await expectNoViolation(page);
  });
}
