import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { record, stubExplainAndConflicts } from "./stubs-access-explain";

const schemes = ["light", "dark"] as const;
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

async function expectNoViolation(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(wcag21aa).analyze();
  expect(
    results.violations.map(
      (violation) =>
        `${violation.id}: ${violation.help} at ${violation.nodes.map((node) => `${node.target.map(String).join(" ")} (${node.failureSummary ?? ""})`).join("; ")}`,
    ),
  ).toEqual([]);
}

/** Types into a searchable field, over what it held, and takes the first option with the keys. */
async function choose(page: Page, field: ReturnType<Page["getByRole"]>, typed: string, option: string): Promise<void> {
  await field.focus();
  await page.keyboard.press("ControlOrMeta+a");
  await page.keyboard.type(typed);
  await expect(page.getByRole("option", { name: option })).toBeVisible();
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("Enter");
}

for (const scheme of schemes) {
  test(`the conflict rules in the ${scheme} scheme: the rules in words, the editor by keyboard, the report, and no WCAG 2.1 AA violation [TF-172 AC1, ADR-0024]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubExplainAndConflicts(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/conflicts");

    // The rules: what each keeps apart and what it does, in words.
    await expect(page.getByRole("heading", { level: 2, name: "Conflict rules" })).toBeVisible();
    await expect(page.getByRole("gridcell", { name: "Shape the tree or read its people" })).toBeVisible();
    await expect(page.getByRole("gridcell", { name: "Blocks" })).toBeVisible();
    await expect(page.getByRole("gridcell", { name: "Warns" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Change" })).toBeDisabled();
    await page.screenshot({
      path: `test-results/a11y/conflicts-rules-${scheme}.png`,
      fullPage: true,
    });
    await expectNoViolation(page);

    // Keyboard only: the editor opens on Enter with the name focused; each side is a kind and a choice by its words.
    const add = page.getByRole("button", { name: "Add a rule" });
    await add.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Add a conflict rule" });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("textbox", { name: "Name" })).toBeFocused();
    await page.keyboard.type("Grant or shape");
    await choose(
      page,
      dialog.getByRole("combobox", { name: "Permission" }).first(),
      "Assign roles",
      "Assign roles to principals and remove assignments (authz.assignments.manage)",
    );
    const second = dialog.getByRole("radiogroup", { name: "The second side" });
    await second.getByRole("radio", { name: "A permission" }).focus();
    await page.keyboard.press("ArrowRight");
    await expect(second.getByRole("radio", { name: "A role" })).toBeChecked();
    await choose(page, dialog.getByRole("combobox", { name: "Role" }), "Operations", "Operations lead");
    await dialog.getByRole("radio", { name: "Blocks" }).focus();
    await page.keyboard.press("ArrowDown");
    await expect(dialog.getByRole("radio", { name: "Warns" })).toBeChecked();
    await page.screenshot({
      path: `test-results/a11y/conflicts-editor-${scheme}.png`,
      fullPage: true,
    });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(add).toBeFocused();

    // The report: when it ran and how, and each conflict with its two grants and where both apply, in words.
    await page.getByRole("tab", { name: "Rules" }).focus();
    await page.keyboard.press("ArrowRight");
    await expect(page.getByRole("tab", { name: "Report" })).toHaveAttribute("aria-selected", "true");
    await expect(page.getByText(/^Last run .* \(the daily run\): 2 conflicts across 2 rules\.$/)).toBeVisible();
    await expect(
      page.getByRole("gridcell", {
        name: "Operations lead at Operations and the units below it, by delegation",
      }),
    ).toBeVisible();
    await expect(page.getByRole("gridcell", { name: "The whole organisation", exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Run the report now" })).toBeEnabled();
    await page.screenshot({
      path: `test-results/a11y/conflicts-report-${scheme}.png`,
      fullPage: true,
    });
    await expectNoViolation(page);
  });

  test(`the grant dialog in the ${scheme} scheme: a conflict rule that refuses the grant, said in words before the confirm, and no WCAG 2.1 AA violation [TF-172 AC1, ADR-0039]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubExplainAndConflicts(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto(`/users/${record.id}`);

    await page.getByRole("tab", { name: "Access" }).click();
    await page.getByRole("button", { name: "Grant a role" }).focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", {
      name: `Grant a role to ${record.display_name}`,
    });
    await expect(dialog).toBeVisible();
    await choose(page, dialog.getByRole("combobox", { name: "Role" }), "User reader", "User reader");

    const notice = dialog.getByRole("status").filter({ hasText: "The conflict rules refuse this grant" });
    await expect(notice).toBeVisible();
    await expect(notice).toContainText("Shape the tree or read its people:");
    await expect(notice).toContainText("Why the rule exists: Who shapes a unit should not also read the people in it.");
    await page.screenshot({
      path: `test-results/a11y/grant-conflict-${scheme}.png`,
      fullPage: true,
    });
    await expectNoViolation(page);
  });
}
