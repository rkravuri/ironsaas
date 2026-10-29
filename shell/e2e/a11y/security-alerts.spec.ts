import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { securityAlert, stubSecurityAlerts } from "./stubs";

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
  test(`the security alerts page in the ${scheme} scheme: the alerts with their severity in words, one opened with its count, its events' link and runbook, its actions by keyboard, and no WCAG 2.1 AA violation [TF-079 AC1, ADR-0056, ADR-0024]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubSecurityAlerts(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto(`/console/security-alerts?alert=${securityAlert.id}`);

    await expect(page.getByRole("heading", { level: 2, name: "Security alerts" })).toBeVisible();
    const grid = page.getByRole("region", { name: "Security alerts" }).getByRole("grid");
    await expect(grid.getByText("A break-glass account opened or used")).toBeVisible();
    await expect(grid.getByText("Critical")).toBeVisible();
    const detail = page.getByRole("region", { name: securityAlert.title });
    await expect(detail).toContainText("112 counted, threshold 100, within 10 minutes");
    await expect(detail).toContainText("Paul Probe");
    await expect(detail.getByRole("link", { name: "Open the events in the audit trail" })).toBeVisible();
    await expect(
      page.getByRole("navigation", { name: "Main navigation" }).getByRole("link", { name: "Security alerts" }),
    ).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/security-alerts-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    // Keyboard only: resolving asks for a note, with the focus on it.
    await detail.getByRole("button", { name: "Resolve" }).focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Resolve the alert?" });
    await expect(dialog.getByLabel(/^Note/)).toBeFocused();
    await page.screenshot({ path: `test-results/a11y/security-alerts-resolve-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");
  });

  test(`the alert rules page in the ${scheme} scheme: each rule's values and state in words, a failing rule's reason, a stricter value's form, and no WCAG 2.1 AA violation [TF-079 AC4, ADR-0056, ADR-0024]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubSecurityAlerts(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/alert-rules?rule=mass_exports");

    await expect(page.getByRole("heading", { level: 2, name: "Alert rules" })).toBeVisible();
    const table = page.getByRole("table", { name: /Each rule/ });
    await expect(table.getByRole("row", { name: /Many permission denials/ })).toContainText(
      "50 events within 10 minutes",
    );
    await expect(table.getByRole("row", { name: /Many permission denials/ })).toContainText(
      "stricter than the operator's",
    );
    await expect(table.getByRole("row", { name: /An export of more than/ })).toContainText("Failing");
    await expect(page.getByText(/The rule "An export of more than 10,000 records" is failing/)).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/alert-rules-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await table.getByRole("button", { name: 'Change "Many permission denials for one principal"' }).focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: 'Change "Many permission denials for one principal"' });
    await expect(dialog.getByLabel("Threshold")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/alert-rules-change-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");
  });
}
