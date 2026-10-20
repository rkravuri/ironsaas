import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { grace, north, stubExplainAndConflicts } from "./stubs-access-explain";

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
  test(`the explanation of access in the ${scheme} scheme: the person, the action and the unit by keyboard, a denial with its first failing check, an allowance with its role, and no WCAG 2.1 AA violation`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubExplainAndConflicts(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/explain");

    await expect(page.getByRole("heading", { level: 2, name: "Explain access" })).toBeVisible();
    await expect(page.getByText("Choose a person and an action")).toBeVisible();

    // Keyboard only: the person by two letters of their name, the action by its words.
    await choose(page, page.getByRole("combobox", { name: "Person" }), "gr", `${grace.name}, ${north.name}`);
    await expect(page.getByRole("combobox", { name: "Person" })).toHaveValue(`${grace.name}, ${north.name}`);
    await choose(
      page,
      page.getByRole("combobox", { name: "Action" }),
      "Import roles",
      "Import roles from another tenant's export.",
    );
    await page.getByRole("button", { name: "Explain" }).focus();
    await page.keyboard.press("Enter");

    // A denial: the outcome, the first check that refuses, and each check in order, in words with a mark.
    await expect(
      page.getByRole("heading", {
        name: `${grace.name} cannot: Import roles from another tenant's export. The system answers 403.`,
      }),
    ).toBeVisible();
    await expect(page.getByText("The first check that refuses it: The permission.")).toBeVisible();
    const checks = page.getByRole("list", { name: "The checks, in order" });
    await expect(checks.getByRole("listitem")).toHaveCount(5);
    await expect(checks).toContainText(
      `${grace.name} does not hold the permission "Create, change and delete tenant roles" for the whole organisation.`,
    );
    await expect(checks).toContainText("The record's policy: not reached");
    await page.screenshot({
      path: `test-results/a11y/explain-denial-${scheme}.png`,
      fullPage: true,
    });
    await expectNoViolation(page);

    // An allowance at a unit: the arrows move on the tree and choose, and the answer names the granting role and its scope.
    await choose(page, page.getByRole("combobox", { name: "Action" }), "Rename", "Rename an organisation unit.");
    const tree = page.getByRole("tree", { name: "At the unit" });
    await expect(tree).toBeVisible();
    await tree.getByRole("treeitem").first().focus();
    for (
      let step = 0;
      step < 10 && !(await page.getByText("Acme Corporation / Operations / North").isVisible());
      step += 1
    ) {
      await page.keyboard.press("ArrowDown");
    }
    await expect(page.getByText("Acme Corporation / Operations / North")).toBeVisible();
    await page.getByRole("button", { name: "Explain" }).focus();
    await page.keyboard.press("Enter");
    await expect(
      page.getByRole("heading", {
        name: `${grace.name} can: Rename an organisation unit.`,
      }),
    ).toBeVisible();
    await expect(page.getByRole("list", { name: "Granted by" })).toContainText(
      "Operations lead, at Operations and the units below it",
    );
    await page.screenshot({
      path: `test-results/a11y/explain-allowance-${scheme}.png`,
      fullPage: true,
    });
    await expectNoViolation(page);
  });
}
