import { expect, test, type Locator } from "@playwright/test";
import { call, open, signIn } from "./helpers";

/**
 * Working across units against a real unit (A8, ADR-0038). `make e2e` sets up the data first
 * (infra/compose/scripts/e2e-data.py): a second and a third branch, each with a colleague, and a person homed at the
 * second who reads the users of both. They choose the unit they work for, and the Users page follows. Then the administrator merges two
 * units made for the run, after reading the preview, and the person homed at one is homed at the other.
 */
const two = { loginId: process.env.E2E_TWO_UNITS_LOGIN_ID ?? "", password: process.env.E2E_TWO_UNITS_PASSWORD ?? "" };
const secondUnit = process.env.E2E_SECOND_UNIT ?? "E2E second branch";
const thirdUnit = process.env.E2E_THIRD_UNIT ?? "E2E third branch";
const tenant = process.env.E2E_TENANT ?? "smoke";
const run = Date.now().toString(36);

const loginCells = (grid: Locator) => grid.locator('[role="gridcell"][col-id="login_id"]');
const control = (name: string) => `Working for ${name}. Choose the unit you work for`;

interface Unit {
  id: string;
  name: string;
  parent_id: string | null;
}

test.describe("working across units", () => {
  test.skip(two.loginId === "", "make e2e sets up the data first");

  test("a person with roles in two units chooses the unit they work for, and the Users page follows", async ({ page }) => {
    await signIn(page, two);
    await open(page, "/users");
    const grid = page.getByRole("region", { name: "Users" });

    // Signed in, they work for their home unit: its people only.
    await expect(page.getByRole("button", { name: control(secondUnit) })).toBeVisible();
    await expect(page.getByText(`The list keeps to ${secondUnit} and the units below it.`, { exact: false })).toBeVisible();
    await expect(loginCells(grid).filter({ hasText: two.loginId })).toBeVisible();
    for (const cell of await loginCells(grid).all()) {
      await expect(cell).toHaveText(/^(two-units|second-colleague)-[0-9a-z]+@/);
    }
    await page.screenshot({ path: "test-results/e2e/working-unit-home.png" });

    await page.getByRole("button", { name: control(secondUnit) }).click();
    await page.getByRole("menu").getByRole("menuitem", { name: new RegExp(`^${thirdUnit}`) }).click();

    await expect(page.getByRole("button", { name: control(thirdUnit) })).toBeVisible();
    await expect(page.getByText(`The list keeps to ${thirdUnit} and the units below it.`, { exact: false })).toBeVisible();
    await expect(loginCells(grid).filter({ hasText: /^third-colleague-/ })).toBeVisible();
    await expect(loginCells(grid).filter({ hasText: two.loginId })).toHaveCount(0);
    for (const cell of await loginCells(grid).all()) {
      await expect(cell).toHaveText(/^third-colleague-[0-9a-z]+@/);
    }
    await page.screenshot({ path: "test-results/e2e/working-unit-other.png" });

    // Back to the home unit, so the next run starts where a new session does.
    await page.getByRole("button", { name: control(thirdUnit) }).click();
    await page.getByRole("menu").getByRole("menuitem", { name: new RegExp(`^${secondUnit}`) }).click();
    await expect(page.getByRole("button", { name: control(secondUnit) })).toBeVisible();
  });

  test("an administrator previews a merge of two units, merges them, and the person homed at one is homed at the other", async ({ page }) => {
    await signIn(page);
    const from = `E2E merge from ${run}`;
    const into = `E2E merge into ${run}`;
    const tree = await call<{ data: Unit[] }>(page, "GET", "/api/v1/organisation-units");
    const root = tree.body.data.find((unit) => unit.parent_id === null);
    expect(root).toBeDefined();
    const intoUnit = await call<Unit>(page, "POST", `/api/v1/organisation-units/${root!.id}/children`, { name: into, code: `e2e-into-${run}`, kind: "unit" });
    const fromUnit = await call<Unit>(page, "POST", `/api/v1/organisation-units/${root!.id}/children`, { name: from, code: `e2e-from-${run}`, kind: "unit" });
    const person = await call<{ user: { id: string } }>(page, "POST", "/api/v1/users", { login_id: `merged-${run}@${tenant}.localhost`, display_name: `Merged person ${run}`, home_unit_id: fromUnit.body.id });
    expect([intoUnit.status, fromUnit.status, person.status]).toEqual([201, 201, 201]);

    await open(page, "/console/units");
    const units = page.getByRole("tree", { name: "Organisation units" });
    await units.getByText(from, { exact: true }).click();
    await page.getByRole("button", { name: "Merge into another unit" }).click();
    const dialog = page.getByRole("dialog", { name: `Merge ${from} into another unit` });
    await dialog.getByLabel("Merge into").fill(into);
    await page.getByRole("option", { name: new RegExp(`${into}$`) }).click();

    // The preview is the API's: the person homed there moves, nothing else is there to move.
    const preview = dialog.getByRole("table");
    await expect(dialog.getByRole("heading", { name: `What moves from ${from} to ${into}` })).toBeVisible();
    await expect(preview).toContainText(`1 person whose home unit becomes ${into}: Merged person ${run}`);
    await page.screenshot({ path: "test-results/e2e/merge-preview.png" });

    await dialog.getByRole("button", { name: `Merge ${from} into ${into}` }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByRole("heading", { name: into })).toBeVisible();
    await units.getByText(from, { exact: true }).click();
    await expect(page.getByText(`This unit was merged into ${into}. What it held is there now.`)).toBeVisible();
    await expect(page.getByRole("button", { name: "Reactivate" })).toHaveCount(0);
    await page.screenshot({ path: "test-results/e2e/merged-unit.png" });

    const moved = await call<{ home_unit: { id: string } | null }>(page, "GET", `/api/v1/users/${person.body.user.id}`);
    expect(moved.body.home_unit?.id).toBe(intoUnit.body.id);
  });
});
