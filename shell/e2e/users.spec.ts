import { expect, test, type Locator } from "@playwright/test";
import { loginId, open, signIn } from "./helpers";

/** The cell of the login column that shows this value; "smoke-client" is also a display name, so the column matters. */
const loginCell = (grid: Locator, value: string) => grid.locator('[role="gridcell"][col-id="login_id"]', { hasText: value });

test.describe("the Users grid", () => {
  test("lists the tenant's users from the API with server paging and sorting", async ({ page }) => {
    await signIn(page);
    // Register the wait before loading the page, so the first request cannot be missed.
    const firstPage = page.waitForRequest((request) => request.url().includes("/api/v1/users?") && request.url().includes("page_number=1"));
    await page.goto("/users");
    const request = await firstPage;
    expect(request.url()).toContain("page_size=50");
    expect(request.url()).toContain("sort=login_id");

    const grid = page.getByRole("region", { name: "Users" });
    await expect(loginCell(grid, "smoke-client")).toBeVisible();
    await expect(loginCell(grid, loginId)).toBeVisible();

    // A header click is a server round trip with the sort in the query: ascending, then descending.
    const sorted = page.waitForRequest((request) => request.url().includes("/api/v1/users?") && request.url().includes("sort=-login_id"));
    const header = grid.locator(".ag-header-cell", { hasText: "Login identifier" });
    await header.getByText("Login identifier", { exact: true }).click();
    await header.getByText("Login identifier", { exact: true }).click();
    await sorted;
  });

  test("a filter becomes a filter term in the query", async ({ page }) => {
    await signIn(page);
    await open(page, "/users");
    const grid = page.getByRole("region", { name: "Users" });
    await expect(loginCell(grid, "smoke-client")).toBeVisible();

    // "smoke-" matches the smoke client only; the administrator's login id holds "smoke." instead.
    const filtered = page.waitForRequest((request) => request.url().includes("filter=login_id%3Acontains%3Asmoke-"));
    const header = grid.locator(".ag-header-cell", { hasText: "Login identifier" });
    await header.hover();
    await header.locator(".ag-header-cell-menu-button, .ag-header-icon.ag-filter-icon, [data-ref='eFilterButton']").first().click();
    await page.locator(".ag-filter input[type='text'], .ag-filter .ag-text-field-input").first().fill("smoke-");
    await page.locator(".ag-filter button", { hasText: /apply/i }).click();
    await filtered;

    await expect(loginCell(grid, "smoke-client")).toBeVisible();
    await expect(loginCell(grid, loginId)).toHaveCount(0);
  });
});

test.describe("the Users grid by keyboard", () => {
  test("selects a row with Space and Enter, opens it with Ctrl+Enter, and the density switch changes the row height", async ({ page }) => {
    await signIn(page);
    await open(page, "/users");
    const grid = page.getByRole("region", { name: "Users" });
    const row = (index: number) => grid.locator(`.ag-row[row-index="${index}"]`).first();
    await expect(loginCell(grid, "smoke-client")).toBeVisible();

    await page.getByRole("radiogroup", { name: "Density" }).getByRole("radio", { name: "Compact" }).focus();
    for (let presses = 0; presses < 8 && (await grid.locator(".ag-cell-focus").count()) === 0; presses += 1) {
      await page.keyboard.press("Tab");
    }
    await expect(grid.locator(".ag-cell-focus")).toHaveCount(1);

    await page.keyboard.press("Space");
    await expect(row(0)).toHaveAttribute("aria-selected", "true");
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");
    await expect(row(1)).toHaveAttribute("aria-selected", "true");
    await expect(row(0)).not.toHaveAttribute("aria-selected", "true");

    // Ctrl+Enter opens the row's record page (A2); back is the list again.
    await page.keyboard.press("Control+Enter");
    await expect(page).toHaveURL(/\/users\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("tab", { name: "Access" })).toBeVisible();
    await page.goBack();
    await expect(row(0)).toBeVisible();

    const comfortable = await row(0).boundingBox();
    await page.getByRole("radiogroup", { name: "Density" }).getByText("Compact").click();
    await expect.poll(async () => (await row(0).boundingBox())?.height).toBe(32);
    expect(comfortable?.height).toBe(40);
  });
});
