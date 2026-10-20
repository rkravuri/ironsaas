import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { stubSettings } from "./stubs";

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
  test(`the settings page in the ${scheme} scheme: by module, each value with its source in words, a unit's inherited value, the change dialog by keyboard, and no WCAG 2.1 AA violation [TF-172 AC1, ADR-0024]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubSettings(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/settings");

    await expect(page.getByRole("heading", { level: 2, name: "Settings" })).toBeVisible();
    await expect(page.getByRole("heading", { level: 3, name: "Sign-in and invitations" })).toBeVisible();
    const idle = page.getByRole("article", { name: "A session ends without activity after" });
    await expect(idle).toContainText("30 minutes");
    await expect(idle).toContainText("Default: 60 minutes");
    await expect(idle).toContainText(/Set here by Ravi Ravuri on /);
    await expect(page.getByRole("article", { name: "An invitation link stays valid for" })).toContainText(
      "The default: nobody has set a value.",
    );
    await expect(
      page.getByRole("navigation", { name: "Main navigation" }).getByRole("link", { name: "Settings" }),
    ).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/settings-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    // Keyboard only: the change dialog opens on Enter, takes focus, and closes with Escape back to its button.
    const change = idle.getByRole("button", { name: "Change: A session ends without activity after" });
    await change.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Change: A session ends without activity after" });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("textbox", { name: "Value" })).toHaveValue("30");
    await expect(dialog).toContainText("A whole number from 15 to 480.");
    await page.screenshot({ path: `test-results/a11y/settings-change-${scheme}.png` });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(change).toBeFocused();

    // A unit chosen on the tree: the value it inherits from the unit above, said with that unit's path.
    // The segmented control's radio sits under its label, which is what a person clicks.
    await page.locator("label", { hasText: "One unit" }).click();
    await expect(page.getByRole("radio", { name: "One unit" })).toBeChecked();
    await page.getByRole("tree").getByText("North", { exact: true }).click();
    const invitation = page.getByRole("article", { name: "An invitation link stays valid for" });
    await expect(invitation).toContainText("14 days");
    await expect(invitation).toContainText("From Acme Corporation / Operations, set by Ravi Ravuri on");
    await page.screenshot({ path: `test-results/a11y/settings-unit-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });
}
