import { expect, test, type Locator } from "@playwright/test";
import { loginId, open, signIn } from "./helpers";

/** The cell of the login column that shows this value; "smoke-client" is also a display name, so the column matters. */
const loginCell = (grid: Locator, value: string) => grid.locator('[role="gridcell"][col-id="login_id"]', { hasText: value });

test.describe("the Users grid", () => {
  test("lists the tenant's users from the API with server paging and sorting", async ({ page }) => {
    await signIn(page);
    // After login the shell lands on /users by itself, so the first request may already be gone: register the wait, then load the page.
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
