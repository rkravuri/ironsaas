import { expect, test } from "@playwright/test";
import { call, open, signIn } from "./helpers";

/**
 * Content visibility against a real unit (TF-241, ADR-0057): the console page defines a tag, the record's tags control
 * puts it on a sample record, and the record's History shows the change with its tags before and after. Sample records
 * exist only in a unit the operator marked for testing (make sample-records), as the release's tenant and echo are.
 */
const run = process.env.E2E_RUN ?? `${Date.now()}`;
const runUnitId = process.env.E2E_RUN_UNIT_ID ?? "";

test.describe("content visibility", () => {
  test.skip(runUnitId === "", "make e2e sets up the run's unit first");

  test("an administrator defines a tag in the console and puts it on a record, whose History shows the change [TF-241 AC7, ADR-0057]", async ({
    page,
  }) => {
    await signIn(page);
    const probe = await call<unknown>(page, "GET", "/api/v1/sample-records?page_size=1");
    test.skip(
      probe.status === 404,
      "the unit does not run the sample module: make sample-records TENANT=x, then make update-unit",
    );

    // The console's page: a new tag, by its code, which is kept in capitals.
    const code = `E2E-${run}`.toUpperCase().slice(0, 32);
    const name = `End-to-end ${run}`;
    await open(page, "/console/visibility");
    await expect(page.getByRole("heading", { level: 2, name: "Content visibility" })).toBeVisible();
    await page.getByRole("button", { name: "New tag" }).click();
    const dialog = page.getByRole("dialog", { name: "New visibility tag" });
    await dialog.getByRole("textbox", { name: "Code" }).fill(code.toLowerCase());
    await dialog.getByRole("textbox", { name: "Name" }).fill(name);
    await dialog.getByRole("button", { name: "Create the tag" }).click();
    await expect(page.getByText(`Created the tag ${name} (${code}).`)).toBeVisible();
    const row = page
      .getByRole("table", { name: "The visibility tags of this organisation" })
      .getByRole("row")
      .filter({ hasText: code });
    await expect(row).toContainText("Active");
    await page.screenshot({ path: "test-results/e2e/visibility-console.png", fullPage: true });

    // A sample record of the run's unit, and the tag put on it through the record's control.
    const created = await call<{ id: string }>(page, "POST", "/api/v1/sample-records", {
      name: `Tagged ${run}`,
      unit_id: runUnitId,
    });
    expect(created.status).toBe(201);
    await open(page, `/samples/${created.body.id}`);
    await expect(page.getByText("No visibility tags: everyone who may see records of its unit sees it.")).toBeVisible();
    await page.getByRole("button", { name: "Change tags" }).click();
    const tags = page.getByRole("dialog", { name: "Change the record's visibility tags" });
    await tags.getByRole("checkbox", { name: `${name} (${code})` }).check();
    await tags.getByRole("button", { name: "Save the tags" }).click();
    await expect(page.getByRole("list", { name: "Visibility tags" })).toContainText(`${name} (${code})`);
    await page.screenshot({ path: "test-results/e2e/visibility-record.png", fullPage: true });

    // The change is the record's own, in its History, before and after.
    await page.getByRole("tab", { name: "History" }).click();
    const change = page.locator('[data-field="visibility_tags"]').first();
    await expect(change).toContainText("None");
    await expect(change).toContainText(code);

    // The tag is retired at the end, so runs leave no active tag behind; the record keeps it, and nobody sees more.
    await open(page, "/console/visibility");
    await page.getByRole("button", { name: `Retire ${name}` }).click();
    await page
      .getByRole("dialog", { name: `Retire ${name}` })
      .getByRole("button", { name: "Retire" })
      .click();
    await expect(page.getByText(`Retired the tag ${name}.`)).toBeVisible();
  });
});
