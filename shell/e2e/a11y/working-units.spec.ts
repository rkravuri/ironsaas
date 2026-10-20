import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { me, sessionFromNow, stubConsole, stubSignedIn, units } from "./stubs";

const schemes = ["light", "dark"] as const;
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

async function expectNoViolation(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(wcag21aa).analyze();
  expect(results.violations.map((violation) => `${violation.id}: ${violation.help} at ${violation.nodes.map((node) => `${node.target.map(String).join(" ")} (${node.failureSummary ?? ""})`).join("; ")}`)).toEqual([]);
}

const [acme, , finance, operations, north, , sales] = units;
const step = (unit: (typeof units)[number]) => ({ id: unit.id, name: unit.name });
const choice = (...path: (typeof units)[number][]) => ({ id: path.at(-1)!.id, name: path.at(-1)!.name, code: path.at(-1)!.code, depth: path.length - 1, path: path.map(step) });

/**
 * A person homed at North with a role at Operations and the units below it, and one at Finance: three units to work
 * for, and all of them at once. The choice is remembered, so /me answers it after, as the API does.
 */
async function stubWorkingUnits(target: Page): Promise<void> {
  await stubSignedIn(target);
  let working: { id: string; name: string; code: string } | "all" = { id: north.id, name: north.name, code: north.code };
  const meNow = () => ({
    ...me,
    session: sessionFromNow(),
    home_unit: { id: north.id, name: north.name, code: north.code },
    working_unit: working === "all" ? { id: north.id, name: north.name, code: north.code } : working,
    all_units: working === "all",
    working_units: { units: [choice(acme, finance), choice(acme, operations), choice(acme, operations, north)], all_units_available: true },
  });
  await target.route("**/api/v1/me", (route) => route.fulfill({ json: meNow() }));
  await target.route("**/api/v1/me/working-unit", (route) => {
    const body = route.request().postDataJSON() as { unit_id?: string; all_units?: boolean };
    const unit = units.find((candidate) => candidate.id === body.unit_id);
    working = body.all_units === true || !unit ? "all" : { id: unit.id, name: unit.name, code: unit.code };
    return route.fulfill({ json: meNow() });
  });
}

for (const scheme of schemes) {
  test(`the choice of the unit you work for in the ${scheme} scheme: the unit in the header, the menu on the tree, the Users page follows, and no WCAG 2.1 AA violation`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubWorkingUnits(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/users");

    await expect(page.getByText("The list keeps to North and the units below it.", { exact: false })).toBeVisible();
    const control = page.getByRole("button", { name: "Working for North. Choose the unit you work for" });
    await expect(control).toBeVisible();

    // Keyboard only: the menu opens on Enter and its first item takes the focus.
    await control.focus();
    await page.keyboard.press("Enter");
    // Mantine names the menu after its button.
    const menu = page.getByRole("menu", { name: "Working for North. Choose the unit you work for" });
    // The words for assistive technology are a separate element, which Chromium reads with a space before them.
    await expect(menu.getByRole("menuitem", { name: /^North\s*, chosen$/ })).toBeVisible();
    await expect(menu.getByRole("menuitem", { name: /Finance/ })).toContainText("Under Acme Corporation");
    await expect(menu.getByRole("menuitem", { name: /All my units/ })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/working-unit-${scheme}.png` });
    await expectNoViolation(page);

    await menu.getByRole("menuitem", { name: /Finance/ }).click();
    await expect(page.getByRole("button", { name: "Working for Finance. Choose the unit you work for" })).toBeVisible();
    await expect(page.getByText("The list keeps to Finance and the units below it.", { exact: false })).toBeVisible();

    await page.getByRole("button", { name: /Choose the unit you work for/ }).click();
    await page.getByRole("menu").getByRole("menuitem", { name: /All my units/ }).click();
    await expect(page.getByRole("button", { name: "Working for All my units. Choose the unit you work for" })).toBeVisible();
    await expect(page.getByText("The list shows all your units.", { exact: false })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/working-unit-users-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`the merge preview in the ${scheme} scheme: the unit to merge into, what moves by kind in words, what stays behind, and no WCAG 2.1 AA violation`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubConsole(page);
    await page.route("**/api/v1/organisation-units/*/merge-preview?**", (route) =>
      route.fulfill({
        json: {
          from: sales,
          into: operations,
          parts: [
            { code: "records", module: "orders", record_type: "sales_order", record_type_name: "Sales orders", count: 128, items: [], conflicts: [] },
            { code: "people", module: "identity", record_type: null, record_type_name: null, count: 14, items: ["Ada Lovelace", "Grace Hopper", "Linus Pauling"], conflicts: [] },
            { code: "assignments", module: "authorization", record_type: null, record_type_name: null, count: 2, items: ["Ada Lovelace (Sales lead)", "Grace Hopper (Reader)"], conflicts: ["Marie Curie (Reader)"] },
            { code: "settings", module: "settings", record_type: null, record_type_name: null, count: 1, items: ["orders.numbering.prefix"], conflicts: ["ui.theme"] },
            { code: "shares", module: "data", record_type: null, record_type_name: null, count: 3, items: ["sales_order"], conflicts: [] },
            { code: "children", module: "authorization", record_type: null, record_type_name: null, count: 2, items: ["Enterprise", "Partners"], conflicts: [] },
          ],
        },
      }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/units");

    const tree = page.getByRole("tree", { name: "Organisation units" });
    await tree.getByText("Sales", { exact: true }).click();
    await expect(page.getByRole("heading", { name: "Sales" })).toBeVisible();
    await expect(page.getByText("No module asks anything of a new unit yet.")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/units-readiness-${scheme}.png` });

    // Keyboard only: the button opens the dialog on Enter, the choice of unit takes the focus, typing narrows it, and the
    // arrow and Enter choose.
    await page.getByRole("button", { name: "Merge into another unit" }).focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Merge Sales into another unit" });
    await expect(dialog).toBeVisible();
    const into = dialog.getByLabel("Merge into");
    await expect(into).toBeFocused();
    await page.keyboard.type("Operations");
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");
    await expect(into).toHaveValue("Acme Corporation / Operations");
    const preview = dialog.getByRole("table");
    await expect(dialog.getByRole("heading", { name: "What moves from Sales to Operations" })).toBeVisible();
    await expect(preview.getByRole("rowheader", { name: "People" })).toBeVisible();
    await expect(preview).toContainText("Sales orders: 128 records, from orders");
    await expect(preview).toContainText("14 people whose home unit becomes Operations: Ada Lovelace, Grace Hopper, Linus Pauling, and 11 more");
    await expect(preview).toContainText("1 is not moved, because Operations has its own value: ui.theme");
    await expect(dialog.getByRole("button", { name: "Merge Sales into Operations" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/units-merge-preview-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(dialog).toBeHidden();
  });

  test(`the sharing control in the ${scheme} scheme: who sees the record in words, the modes and the units on the tree, and no WCAG 2.1 AA violation`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.route("**/api/**", (route) => route.fulfill({ status: 401, contentType: "application/problem+json", body: JSON.stringify({ status: 401, error_code: "identity.unauthenticated" }) }));
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto(`/gallery?scheme=${scheme}`);
    await expect(page.locator("[data-gallery-ready='true']")).toBeAttached();

    const record = page.locator("#gallery-record-page");
    await expect(record.getByText("Belongs to North and is shared with Finance: the people whose roles cover any of them see it.")).toBeVisible();
    await record.locator("[data-sharing]").screenshot({ path: `test-results/a11y/sharing-${scheme}.png` });

    // Keyboard only: the button opens the dialog, the chosen mode takes the focus, the units are checkboxes on the tree.
    const open = record.getByRole("button", { name: "Change who sees this" });
    await open.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Who sees this record" });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("radio", { name: "Shared with chosen units" })).toBeChecked();
    const units = dialog.getByRole("group", { name: "Share with" });
    await expect(units.getByRole("checkbox", { name: "Finance" })).toBeChecked();
    // The tree in nested lists: South sits in the list inside Sales's item, beside North, the record's own unit, in words.
    const salesItem = units.getByRole("checkbox", { name: "Sales", exact: true }).locator("xpath=ancestor::li[1]");
    await expect(salesItem.getByRole("list").getByRole("checkbox", { name: "South" })).toBeVisible();
    await expect(salesItem.getByRole("list")).toContainText("North, the record's own unit, sees it whatever you choose");

    // Keyboard only: Tab from the chosen mode to the units, Space ticks one, and Tab and Enter save.
    const south = units.getByRole("checkbox", { name: "South" });
    for (let presses = 0; presses < 8 && !(await south.evaluate((element) => element === document.activeElement)); presses += 1) {
      await page.keyboard.press("Tab");
    }
    await expect(south).toBeFocused();
    await page.keyboard.press("Space");
    await expect(south).toBeChecked();
    await page.screenshot({ path: `test-results/a11y/sharing-dialog-${scheme}.png` });
    await expectNoViolation(page);

    const save = dialog.getByRole("button", { name: "Save" });
    for (let presses = 0; presses < 8 && !(await save.evaluate((element) => element === document.activeElement)); presses += 1) {
      await page.keyboard.press("Tab");
    }
    await expect(save).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(dialog).toBeHidden();
    await expect(record.getByText("Belongs to North and is shared with South and Finance: the people whose roles cover any of them see it.")).toBeVisible();
  });
}
